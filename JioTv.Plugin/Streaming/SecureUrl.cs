using System;
using System.Security.Cryptography;

namespace JioTv.Plugin.Streaming;

/// <summary>
/// AES-CTR URL tokenization surface. Port of JioTV Go's pkg/secureurl:
/// upstream manifest URLs are AES-CTR encrypted with a server-random key into
/// opaque 'auth=' params that only this server can decrypt. Two modes:
/// randomized IV (Encrypt) and a SHA-256-derived IV so identical inputs give
/// byte-identical manifests (EncryptDeterministic — matches Go behavior used
/// to keep Shaka/players from re-downloading segments).
/// </summary>
public interface ISecureUrlCipher
{
    /// <summary>Encrypts a URL with a fresh random IV (URL-safe base64 output).</summary>
    string Encrypt(string inputUrl);

    /// <summary>Encrypts a URL deterministically (stable nonce derived from key + input).</summary>
    string EncryptDeterministic(string inputUrl);

    /// <summary>Decrypts a payload produced by either Encrypt method.</summary>
    string Decrypt(string encryptedUrl);
}

/// <summary>
/// Default AES-256-CTR implementation. Counter mode is not built into .NET,
/// so the CTR keystream is produced manually via one AES-ECB invocation per
/// 16-byte block (see <see cref="CtrProcessForTesting"/> / internal CtrProcess).
/// </summary>
public sealed class SecureUrlCipher : ISecureUrlCipher
{
    private readonly byte[] _key;
    private readonly bool _disabled;

    private SecureUrlCipher(byte[] key, bool disabled)
    {
        _key = key;
        _disabled = disabled;
    }

    /// <summary>Creates a cipher with a fresh random 256-bit key.</summary>
    public static SecureUrlCipher CreateRandom()
    {
        var key = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(key);
        return new SecureUrlCipher(key, disabled: false);
    }

    /// <summary>Mirror of Go's disable_url_encryption mode: query-escape only.</summary>
    public static SecureUrlCipher CreateDisabled() => new([], disabled: true);

    /// <summary>Production AES cipher with caller-supplied key (used by tests / loaded from disk).</summary>
    public static SecureUrlCipher CreateWithKey(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("AES-256 key must be 32 bytes", nameof(key));
        }

        return new SecureUrlCipher((byte[])key.Clone(), disabled: false);
    }

    /// <summary>Builds a cipher whose deterministic IV derives from the key and input URL.</summary>
    private byte[] EncryptCore(string inputUrl, byte[] iv)
    {
        var input = System.Text.Encoding.UTF8.GetBytes(inputUrl);
        var ciphertext = new byte[16 + input.Length];
        Array.Copy(iv, 0, ciphertext, 0, 16);
        var stream = AesCtr(_key, iv);
        var payload = stream.Process(input);
        Array.Copy(payload, 0, ciphertext, 16, payload.Length);
        return ciphertext;
    }

    /// <inheritdoc/>
    public string Encrypt(string inputUrl)
    {
        if (_disabled)
        {
            return Uri.EscapeDataString(inputUrl);
        }

        var iv = new byte[16];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(iv);
        }

        return ToUrlSafeBase64(EncryptCore(inputUrl, iv));
    }

    /// <inheritdoc/>
    public string EncryptDeterministic(string inputUrl)
    {
        if (_disabled)
        {
            return Uri.EscapeDataString(inputUrl);
        }

        // Like Go: sha256(key || inputURL)[:16] as the nonce; identical input
        // totals identical ciphertext, different inputs get different streams.
        var inputBytes = System.Text.Encoding.UTF8.GetBytes(inputUrl);
        var msg = new byte[_key.Length + inputBytes.Length];
        Array.Copy(_key, 0, msg, 0, _key.Length);
        Array.Copy(inputBytes, 0, msg, _key.Length, inputBytes.Length);
        var hash = SHA256.HashData(msg);
        var iv = hash.AsSpan(0, 16).ToArray();
        return ToUrlSafeBase64(EncryptCore(inputUrl, iv));
    }

    /// <inheritdoc/>
    public string Decrypt(string encryptedUrl)
    {
        if (_disabled)
        {
            return Uri.UnescapeDataString(encryptedUrl);
        }

        var ciphertext = FromUrlSafeBase64(encryptedUrl);
        if (ciphertext.Length < 16)
        {
            throw new CryptographicException("ciphertext too short");
        }

        var iv = ciphertext.AsSpan(0, 16).ToArray();
        var body = ciphertext.AsSpan(16).ToArray();
        var stream = AesCtr(_key, iv);
        var plaintext = stream.Process(body);
        return System.Text.Encoding.UTF8.GetString(plaintext);
    }

    private static string ToUrlSafeBase64(byte[] data) => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_'); // ?

    private static byte[] FromUrlSafeBase64(string data)
    {
        var padded = data.Length % 4 == 0 ? data : data + new string('=', 4 - (data.Length % 4));
        return Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'));
    }

    /// <summary>
    /// Manual AES-CTR keystream processing. Exposed for the openssl
    /// cross-language vector test only; internal equivalent CtrProcess used
    /// in production paths above.
    /// </summary>
    public static byte[] CtrProcessForTesting(byte[] key, byte[] iv, byte[] input) => AesCtr(key, iv).Process(input);

    private static AesCtrStream AesCtr(byte[] key, byte[] iv) => new(key, iv);

    private sealed class AesCtrStream
    {
        private readonly Aes _aes;
        private readonly byte[] _iv;

        public AesCtrStream(byte[] key, byte[] iv)
        {
            _aes = Aes.Create();
            _aes.Key = key;
#pragma warning disable CA5358 // ECB used only as raw block cipher primitive to build CTR keystream; not encrypting data directly
            _aes.Mode = CipherMode.ECB;
#pragma warning restore CA5358
            _aes.Padding = PaddingMode.None;
            _iv = (byte[])iv.Clone();
        }

        public byte[] Process(byte[] input)
        {
            var output = new byte[input.Length];
            var counter = (byte[])_iv.Clone();
            var counterBlock = new byte[16];
            int offset = 0;

            using var encryptor = _aes.CreateEncryptor();
            while (offset < input.Length)
            {
                _ = encryptor.TransformBlock(counter, 0, 16, counterBlock, 0);
                var chunk = Math.Min(16, input.Length - offset);
                for (var i = 0; i < chunk; i++)
                {
                    output[offset + i] = (byte)(input[offset + i] ^ counterBlock[i]);
                }

                offset += chunk;

                // increment the 128-bit big-endian counter
                for (var i = 15; i >= 0; i--)
                {
                    if (++counter[i] != 0)
                    {
                        break;
                    }
                }
            }

            return output;
        }
    }
}
