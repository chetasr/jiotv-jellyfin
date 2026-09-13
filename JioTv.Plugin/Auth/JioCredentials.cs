using System.Text.Json.Serialization;

namespace JioTv.Plugin.Auth;

/// <summary>
/// Stored JioTV credentials. JSON field names match JioTV Go's
/// pkg/utils/types.go JIOTV_CREDENTIALS so files stay mutually readable.
/// </summary>
public class JioCredentials
{
    [JsonPropertyName("ssoToken")]
    public string SSOToken { get; set; } = string.Empty;

    [JsonPropertyName("uniqueId")]
    public string UniqueId { get; set; } = string.Empty;

    [JsonPropertyName("crm")]
    public string CRM { get; set; } = string.Empty;

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("lastTokenRefreshTime")]
    public string LastTokenRefreshTime { get; set; } = string.Empty;

    [JsonPropertyName("lastSSOTokenRefreshTime")]
    public string LastSSOTokenRefreshTime { get; set; } = string.Empty;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("mobileNumber")]
    public string MobileNumber { get; set; } = string.Empty;
}
