using System;
using JioTv.Plugin.Streaming;
using Xunit;

namespace JioTv.Tests;

/// <summary>
/// SecureUrl port tests: round-trips, determinism, decryption of both IV
/// forms, failure modes, and a cross-language ciphertext vector produced by
/// `openssl enc -aes-256-ctr` to prove byte-compatibility with Go's
/// crypto/cipher.NewCTR implementation.
/// </summary>
public class SecureUrlTests
{
    // Fixed key for reproducible cross-language vectors.
    private static readonly byte[] TestKey =
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
    };

    private static ISecureUrlCipher CipherWithTestKey() => SecureUrlCipher.CreateWithKey(TestKey);

    [Theory]
    [InlineData("https://example.com/test")]
    [InlineData("https://example.com/test?param1=value1&param2=value2")]
    [InlineData("")]
    public void EncryptDecrypt_RoundTrip(string url)
    {
        var cipher = CipherWithTestKey();
        var encrypted = cipher.Encrypt(url);
        Assert.Equal(url, cipher.Decrypt(encrypted));
    }

    [Fact]
    public void EncryptDeterministic_SameInputSameOutput()
    {
        var cipher = CipherWithTestKey();
        var url = "https://edge.tv/HLSV3/144/index.m3u8?__hdnea__=exp=1~hmac=99";
        Assert.Equal(cipher.EncryptDeterministic(url), cipher.EncryptDeterministic(url));
    }

    [Fact]
    public void Decrypt_HandlesShortCiphertext()
    {
        var cipher = CipherWithTestKey();
        Assert.ThrowsAny<Exception>(() => cipher.Decrypt("AA=="));
    }

    [Fact]
    public void Decrypt_InvalidBase64_Throws()
    {
        var cipher = CipherWithTestKey();
        Assert.ThrowsAny<Exception>(() => cipher.Decrypt("invalid-base64!"));
    }

    [Fact]
    public void CtrPattern_MatchesOpenSslReferenceVector()
    {
        // openssl enc -aes-256-ctr -K 000102...  -iv 000...001
        var cipher = CipherWithTestKey();
        var iv = new byte[16];
        iv[15] = 0x01;
        var input = "https://example.com/test?param1=value1";
        var raw = SecureUrlCipher.CtrProcessForTesting(TestKey, iv, System.Text.Encoding.UTF8.GetBytes(input));
        var encoded = System.Convert.ToBase64String(raw)
            .Replace('+', '-').Replace('/', '_');
        Assert.Equal(
            "mCkC3jmDsMrDjvpcOK5TE23T2PHBSfDJN9jIR3lBoKSkIjomTbA=".Replace('+', '-').Replace('/', '_'),
            encoded);
    }

    [Fact]
    public void DisableMode_QueryEscapesInstead()
    {
        var cipher = SecureUrlCipher.CreateDisabled();
        var url = "https://example.com/test?a=b&c=d";
        Assert.Equal(Uri.EscapeDataString(url), cipher.Encrypt(url));
        Assert.Equal(Uri.EscapeDataString(url), cipher.EncryptDeterministic(url));
        Assert.Equal(url, cipher.Decrypt(Uri.EscapeDataString(url)));
    }
}
