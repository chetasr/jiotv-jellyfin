using System;
using JioTv.Plugin.Auth;
using Xunit;

namespace JioTv.Tests;

/// <summary>
/// Ports of JioTV Go's internal/handlers/token_validity_test.go cases.
/// Same semantics: JWT exp drives refresh with lead buffers; fallback TTLs
/// apply when exp can't be parsed.
/// </summary>
public class TokenValidityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);

    private static string MakeJwt(long expUnix)
    {
        var payload = $"{{\"exp\":{expUnix}}}";
        return $"header.{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.sig";
    }

    [Fact]
    public void EmptyToken_ShouldRefresh()
    {
        Assert.True(TokenValidity.ShouldRefreshToken("", "", DateTime.UtcNow));
    }

    [Fact]
    public void JwtExpired_ShouldRefresh()
    {
        var token = MakeJwt(((DateTimeOffset)Now).ToUnixTimeSeconds() - 100);
        Assert.True(TokenValidity.ShouldRefreshToken(token, "", Now.UtcDateTime));
    }

    [Fact]
    public void JwtValidForMinutes_ShouldRefresh_WithinLeadTime()
    {
        // Lead time = 30s; token expires in 20s => refresh
        var token = MakeJwt(((DateTimeOffset)Now).ToUnixTimeSeconds() + 20);
        Assert.True(TokenValidity.ShouldRefreshToken(token, "", Now.UtcDateTime));
    }

    [Fact]
    public void JwtValidForMinutes_ShouldNotRefresh_WithHeadroom()
    {
        var token = MakeJwt(((DateTimeOffset)Now).ToUnixTimeSeconds() + 3600);
        Assert.False(TokenValidity.ShouldRefreshToken(token, "", Now.UtcDateTime));
    }

    [Fact]
    public void UnparseableJwt_ValidLastRefresh_UsesFallbackTtl()
    {
        // AccessToken fallback: TTL 2h, lead 10min => threshold last+110min.
        // Refreshed 10 minutes ago => not expired.
        Assert.False(TokenValidity.ShouldRefreshToken(
            "not-a-jwt", "1699999940", // lastRefresh = now - lastRefresh unix
            DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime));
    }

    [Fact]
    public void UnparseableJwt_StaleLastRefresh_UsesFallbackTtl()
    {
        Assert.True(TokenValidity.ShouldRefreshToken(
            "not-a-jwt", "1699992340", // lastRefresh = now - 7660s (>110min)
            DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime));
    }

    [Fact]
    public void UnparseableJwt_NoLastRefresh_ShouldRefresh()
    {
        Assert.True(TokenValidity.ShouldRefreshToken("not-a-jwt", "", Now.UtcDateTime));
    }

    [Fact]
    public void SsoFallbackTtl_24hLead1h()
    {
        DateTimeOffset refreshTime = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        // 20 minutes ago => not stale
        Assert.False(TokenValidity.ShouldRefreshSsoToken("not-a-jwt",
            refreshTime.AddSeconds(-1200).ToUnixTimeSeconds().ToString(), refreshTime.UtcDateTime));
        // 80 minutes after (fallbackTTL 24h - lead 1h, only after 23h... )
        // flight check: threshold = last + 23h; last+80min < threshold => still fine
        Assert.False(TokenValidity.ShouldRefreshSsoToken("not-a-jwt",
            refreshTime.AddSeconds(2 * 3600).ToUnixTimeSeconds().ToString(), refreshTime.UtcDateTime));
    }
}
