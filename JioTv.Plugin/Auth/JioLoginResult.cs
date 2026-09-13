using System.Text.Json.Serialization;

namespace JioTv.Plugin.Auth;

/// <summary>
/// Result of the OTP login flow, mirroring JioTV Go's handler payloads.
/// </summary>
public class JioLoginResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
