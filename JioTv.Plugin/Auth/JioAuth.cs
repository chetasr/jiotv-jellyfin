using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JioTv.Plugin.Network;

#pragma warning disable CA1031 // Intentionally broad catch for external API errors in auth path (logged)

namespace JioTv.Plugin.Auth;

/// <summary>
/// JioTV OTP login and background token refresh. Port of JioTV Go's
/// internal/handlers/auth.go + pkg/utils/utils.go login functions.
/// </summary>
public sealed class JioAuth : IDisposable
{
    private const string LoginOtpSendUrl = "https://" + JioConstants.JioTvApiDomain + "/userservice/apis/v1/loginotp/send";
    private const string LoginOtpVerifyUrl = "https://" + JioConstants.JioTvApiDomain + "/userservice/apis/v1/loginotp/verify";

    private readonly CredentialStore _store;
    private readonly SemaphoreSlim _refreshMutex = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="JioAuth"/> class.
    /// </summary>
    /// <param name="store">Credential store for persistence.</param>
    public JioAuth(CredentialStore store)
    {
        _store = store;
    }

    /// <summary>Retention of the mobile number between SendOtp and VerifyOtp call sites (informational).</summary>
    public event EventHandler<string>? OnCredentialsChanged;

    /// <summary>
    /// Sends an OTP to <paramref name="mobileNumber"/> (raw digits, e.g. 10-digit Indian mobile).
    /// Returns true when Jio accepted the send (HTTP 204).
    /// </summary>
    public async Task<bool> SendOtpAsync(string mobileNumber)
    {
        var payload = JsonSerializer.Serialize(new SendOtpPayload
        {
            Number = ToBase64("+91" + mobileNumber),
        });

        using var request = JioHttp.BuildJsonPost(LoginOtpSendUrl, payload);
        request.Headers.TryAddWithoutValidation("appname", "RJIL_JioTV");
        request.Headers.TryAddWithoutValidation("os", JioConstants.OsAndroid);
        request.Headers.TryAddWithoutValidation("devicetype", JioConstants.DeviceTypePhone);
        request.Headers.TryAddWithoutValidation("User-Agent", JioConstants.UserAgentOkHttp);

        using var response = await JioHttp.HttpClient.SendAsync(request).ConfigureAwait(false);
        if ((int)response.StatusCode == 204)
        {
            return true;
        }

        throw CreateExternalError($"OTP send failed with status {(int)response.StatusCode}");
    }

    /// <summary>
    /// Verifies OTP for given mobile number, stores credentials on success.
    /// Returns descriptive result with status=success or status=failed.
    /// </summary>
    public async Task<JioLoginResult> VerifyOtpAsync(string mobileNumber, string otp)
    {
        var deviceId = EnsureDeviceId(_store);
        var payload = JsonSerializer.Serialize(new VerifyOtpPayload
        {
            Number = ToBase64("+91" + mobileNumber),
            OTP = otp,
            DeviceInfo = new VerifyOtpDevice
            {
                ConsumptionDeviceName = "SM-G930F",
                Info = new VerifyOtpDeviceInfo
                {
                    Type = "android",
                    Platform = new VerifyOtpPlatform { Name = "SM-G930F" },
                    AndroidId = deviceId,
                },
            },
        });

        using var request = JioHttp.BuildJsonPost(LoginOtpVerifyUrl, payload);
        request.Headers.TryAddWithoutValidation("appname", "RJIL_JioTV");
        request.Headers.TryAddWithoutValidation("os", JioConstants.OsAndroid);
        request.Headers.TryAddWithoutValidation("devicetype", JioConstants.DeviceTypePhone);
        request.Headers.TryAddWithoutValidation("User-Agent", JioConstants.UserAgentOkHttp);

        using var response = await JioHttp.HttpClient.SendAsync(request).ConfigureAwait(false);
        if ((int)response.StatusCode != 200)
        {
            return new JioLoginResult { Status = "failed", Message = $"Invalid OTP ({(int)response.StatusCode})" };
        }

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<JioVerifyResponse>(body);
        if (result is null || string.IsNullOrEmpty(result.AuthToken))
        {
            return new JioLoginResult { Status = "failed", Message = "Invalid OTP" };
        }

        var creds = new JioCredentials
        {
            SSOToken = result.SSOToken ?? string.Empty,
            CRM = result.SessionAttributes?.User?.SubscriberId ?? string.Empty,
            UniqueId = result.SessionAttributes?.User?.Unique ?? string.Empty,
            AccessToken = result.AuthToken,
            RefreshToken = result.RefreshToken ?? string.Empty,
            LastTokenRefreshTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            DeviceId = deviceId,
            MobileNumber = mobileNumber,
        };
        _store.Save(creds);
        OnCredentialsChanged?.Invoke(this, creds.AccessToken);

        return new JioLoginResult { Status = "success" };
    }

    /// <summary>
    /// Refreshes the access token using the stored refresh token; rewrites
    /// credentials on success. Port of LoginRefreshAccessToken.
    /// </summary>
    public async Task<bool> RefreshAccessTokenAsync()
    {
        var creds = _store.Load();
        if (creds is null)
        {
            throw new InvalidOperationException("No credentials stored; cannot refresh");
        }

        if (string.IsNullOrEmpty(creds.RefreshToken))
        {
            throw new InvalidOperationException("RefreshToken is empty, cannot refresh AccessToken");
        }

        var payload = JsonSerializer.Serialize(new RefreshPayload
        {
            AppName = "RJIL_JioTV",
            DeviceId = creds.DeviceId,
            RefreshToken = creds.RefreshToken,
        });

        using var request = JioHttp.BuildJsonPost(JioConstants.RefreshTokenUrl, payload);
        request.Headers.TryAddWithoutValidation("devicetype", JioConstants.DeviceTypePhone);
        request.Headers.TryAddWithoutValidation("versionCode", JioConstants.VersionCode);
        request.Headers.TryAddWithoutValidation("os", JioConstants.OsAndroid);
        request.Headers.TryAddWithoutValidation("contentType", JioConstants.ContentTypeJson);
        request.Headers.TryAddWithoutValidation("Host", JioConstants.AuthMediaDomain);
        request.Headers.TryAddWithoutValidation("User-Agent", JioConstants.UserAgentOkHttp);
        request.Headers.TryAddWithoutValidation("accessToken", creds.AccessToken);

        using var response = await JioHttp.HttpClient.SendAsync(request).ConfigureAwait(false);
        if ((int)response.StatusCode != 200)
        {
            return false;
        }

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<RefreshResponse>(body);
        if (string.IsNullOrEmpty(result?.AuthToken))
        {
            return false;
        }

        creds.AccessToken = result.AuthToken;
        creds.LastTokenRefreshTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        _store.Save(creds);
        OnCredentialsChanged?.Invoke(this, creds.AccessToken);

        return true;
    }

    /// <summary>
    /// Checks AccessToken and SSOToken freshness and refreshes whichever is
    /// stale, serialised through a mutex to avoid concurrent refresh races
    /// (equivalent to JioTV Go's tokenRefreshMutex).
    /// </summary>
    public async Task EnsureFreshTokensAsync()
    {
        await _refreshMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            var creds = _store.Load();
            if (creds is null || string.IsNullOrEmpty(creds.AccessToken))
            {
                throw new InvalidOperationException("No valid credentials available");
            }

            var now = DateTime.UtcNow;
            if (TokenValidity.ShouldRefreshToken(creds.AccessToken, creds.LastTokenRefreshTime, now))
            {
                await RefreshAccessTokenAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _refreshMutex.Release();
        }
    }

    /// <summary>Releases the refresh mutex.</summary>
    public void Dispose()
    {
        _refreshMutex.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string ToBase64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Creates and persists a random device ID on first use (mirrors
    /// JioTV Go's GetDeviceID behavior).
    /// </summary>
    private static string EnsureDeviceId(CredentialStore store)
    {
        var creds = store.Load();
        if (creds is not null && !string.IsNullOrEmpty(creds.DeviceId))
        {
            return creds.DeviceId;
        }

        var id = Guid.NewGuid().ToString("N");
        if (creds is null)
        {
            creds = new JioCredentials();
        }

        creds.DeviceId = id;
        store.Save(creds);
        return id;
    }

    private static ExternalApiException CreateExternalError(string message) => new(message);

    // --- request/response model types ---

    private sealed class SendOtpPayload
    {
        [JsonPropertyName("number")]
        public string Number { get; set; } = string.Empty;
    }

    private sealed class RefreshPayload
    {
        [JsonPropertyName("appName")]
        public string AppName { get; set; } = string.Empty;

        [JsonPropertyName("deviceId")]
        public string DeviceId { get; set; } = string.Empty;

        [JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; } = string.Empty;
    }

    private sealed class RefreshResponse
    {
        [JsonPropertyName("authToken")]
        public string? AuthToken { get; set; }
    }

    private sealed class VerifyOtpPayload
    {
        [JsonPropertyName("number")]
        public string Number { get; set; } = string.Empty;

        [JsonPropertyName("otp")]
        public string OTP { get; set; } = string.Empty;

        [JsonPropertyName("deviceInfo")]
        public VerifyOtpDevice DeviceInfo { get; set; } = new();
    }

    private sealed class VerifyOtpDevice
    {
        [JsonPropertyName("consumptionDeviceName")]
        public string ConsumptionDeviceName { get; set; } = string.Empty;

        [JsonPropertyName("info")]
        public VerifyOtpDeviceInfo Info { get; set; } = new();
    }

    private sealed class VerifyOtpDeviceInfo
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("platform")]
        public VerifyOtpPlatform Platform { get; set; } = new();

        [JsonPropertyName("androidId")]
        public string AndroidId { get; set; } = string.Empty;
    }

    private sealed class VerifyOtpPlatform
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    private sealed class JioVerifyResponse
    {
        [JsonPropertyName("authToken")]
        public string? AuthToken { get; set; }

        [JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("ssoToken")]
        public string? SSOToken { get; set; }

        [JsonPropertyName("sessionAttributes")]
        public JioSessionAttributes? SessionAttributes { get; set; }
    }

    private sealed class JioSessionAttributes
    {
        [JsonPropertyName("user")]
        public JioUser? User { get; set; }
    }

    private sealed class JioUser
    {
        [JsonPropertyName("subscriberId")]
        public string? SubscriberId { get; set; }

        [JsonPropertyName("unique")]
        public string? Unique { get; set; }
    }
}


