using System;

namespace JioTv.Plugin.Auth;

/// <summary>
/// Token refresh decision logic, ported from JioTV Go's
/// internal/handlers/token_validity.go. JWT exp drives refresh with lead
/// buffers; when the JWT can't be parsed, file-recorded refresh timestamps
/// with fallback TTLs are used instead.
/// </summary>
public static class TokenValidity
{
    /// <summary>Refresh an AccessToken this much before JWT exp.</summary>
    public static readonly TimeSpan JwtTokenRefreshLeadTime = TimeSpan.FromSeconds(30);

    /// <summary>Assumed AccessToken lifetime when exp can't be parsed.</summary>
    public static readonly TimeSpan AccessTokenFallbackTtl = TimeSpan.FromHours(2);

    /// <summary>AccessToken fallback refresh lead (refresh 10min early).</summary>
    public static readonly TimeSpan AccessTokenFallbackLeadTime = TimeSpan.FromMinutes(10);

    /// <summary>Assumed SSOToken lifetime when exp can't be parsed.</summary>
    public static readonly TimeSpan SsoTokenFallbackTtl = TimeSpan.FromHours(24);

    /// <summary>SSOToken fallback refresh lead.</summary>
    public static readonly TimeSpan SsoTokenFallbackLeadTime = TimeSpan.FromHours(1);

    /// <summary>
    /// Decides whether an access token needs refresh.
    /// </summary>
    /// <param name="token">The access token (JWT or opaque).</param>
    /// <param name="lastRefreshUnixSeconds">Unix-seconds string of last refresh; used as fallback when JWT exp can't be parsed.</param>
    /// <param name="now">Current UTC time injected for testability.</param>
    /// <returns>True when the token should be refreshed.</returns>
    public static bool ShouldRefreshToken(string token, string lastRefreshUnixSeconds, DateTime now)
        => ShouldRefreshTokenCore(
            token,
            lastRefreshUnixSeconds,
            JwtTokenRefreshLeadTime,
            AccessTokenFallbackTtl,
            AccessTokenFallbackLeadTime,
            now);

    /// <summary>
    /// SSOToken variant: same logic; JWT lead skipped in favor of the SSO
    /// fallback TTL/lead (ssoToken typically has no exp or a different one).
    /// </summary>
    public static bool ShouldRefreshSsoToken(string token, string lastRefreshUnixSeconds, DateTime now)
        => ShouldRefreshTokenCore(
            token,
            lastRefreshUnixSeconds,
            TimeSpan.FromSeconds(30),
            SsoTokenFallbackTtl,
            SsoTokenFallbackLeadTime,
            now);

    private static bool ShouldRefreshTokenCore(
        string token,
        string lastRefreshUnixSeconds,
        TimeSpan jwtLead,
        TimeSpan fallbackTtl,
        TimeSpan fallbackLead,
        DateTime now)
    {
        if (string.IsNullOrEmpty(token))
        {
            return true;
        }

        if (TryParseJwtExpiry(token, out var expiry))
        {
            return expiry <= now.Add(jwtLead);
        }

        if (!TryThresholdFromLastRefresh(lastRefreshUnixSeconds, fallbackTtl, fallbackLead, out var threshold))
        {
            return true;
        }

        return now >= threshold;
    }

    /// <summary>Parses JWT exp from a compact JWS token.</summary>
    public static bool TryParseJwtExpiry(string token, out DateTime expiryUtc)
    {
        expiryUtc = default;
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        string payload;
        try
        {
            payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Base64UrlToStandard(parts[1])));
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("exp", out var expElement))
            {
                return false;
            }

            if (expElement.ValueKind != System.Text.Json.JsonValueKind.Number)
            {
                return false;
            }

            var expUnix = expElement.GetInt64();
            if (expUnix <= 0)
            {
                return false;
            }

            expiryUtc = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string Base64UrlToStandard(string component)
    {
        var padded = component;
        var remainder = component.Length % 4;
        if (remainder == 2)
        {
            padded += "==";
        }
        else if (remainder == 3)
        {
            padded += "=";
        }

        return padded.Replace('-', '+').Replace('_', '/');
    }

    /// <summary>Computes fallback threshold: last refresh + TTL - lead.</summary>
    private static bool TryThresholdFromLastRefresh(
        string lastRefreshUnixSeconds,
        TimeSpan fallbackTtl,
        TimeSpan fallbackLead,
        out DateTime thresholdUtc)
    {
        thresholdUtc = default;
        if (!long.TryParse(lastRefreshUnixSeconds, out var lastRefreshUnix))
        {
            return false;
        }

        thresholdUtc = DateTimeOffset
            .FromUnixTimeSeconds(lastRefreshUnix)
            .UtcDateTime
            .Add(fallbackTtl)
            .Add(-fallbackLead);
        return true;
    }
}
