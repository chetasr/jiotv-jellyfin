using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace JioTv.Plugin.Streaming;

/// <summary>
/// Per-channel __hdnea__ token cache + dead-channel recovery cache.
/// Port of JioTV Go's renderHDNEACache / renderChannelDeadCache in
/// internal/handlers/handlers.go. Tokens expire fast upstream (~90-120s),
/// so the cache TTL is aggressive (60s) just like Go.
/// </summary>
public sealed class HdneaCache
{
    /// <summary>Matches Go's hdneaCacheTTL.</summary>
    public static readonly TimeSpan TokenTtl = TimeSpan.FromSeconds(60);

    /// <summary>Matches Go's renderChannelDeadCacheTTL (mirrors the JioTV app's own dead-channel behavior).</summary>
    public static readonly TimeSpan DeadChannelTtl = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (string Token, DateTime UpdatedAt)> _tokens = new();
    private readonly ConcurrentDictionary<string, DateTime> _dead = new();
    private readonly Func<DateTime> _now;

    /// <summary>Creates a cache; an optional clock keeps tests deterministic.</summary>
    public HdneaCache(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>
    /// Namespaces cached tokens by stream kind — live vs catchup URLs are
    /// signed with different ACLs, so one token must never be replayed
    /// against the other (Go's hdneaCacheKey).
    /// </summary>
    public static string CacheKey(string channelId, string streamUrl)
    {
        if (string.IsNullOrEmpty(channelId))
        {
            return string.Empty;
        }

        if (streamUrl.Contains("catchup", StringComparison.OrdinalIgnoreCase) ||
            streamUrl.Contains("CATCHUP", StringComparison.Ordinal))
        {
            return channelId + "|catchup";
        }

        return channelId;
    }

    /// <summary>Parses the remaining lifetime from a ~-separated Akamai token's exp= segment.</summary>
    public static TimeSpan? RemainingLifetime(string token, DateTime now)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        foreach (var part in token.Split('~'))
        {
            if (part.StartsWith("exp=", StringComparison.Ordinal)
                && long.TryParse(part.AsSpan(4), System.Globalization.CultureInfo.InvariantCulture, out var expUnix))
            {
                var expiry = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
                return expiry - now;
            }
        }

        return null;
    }

    /// <summary>Stores a fresh token for a cache key (Go's setCachedHDNEA).</summary>
    public void Set(string cacheKey, string token)
    {
        if (string.IsNullOrEmpty(cacheKey) || string.IsNullOrEmpty(token))
        {
            return;
        }

        _tokens[cacheKey] = (token, _now());
    }

    /// <summary>Fetches a still-fresh token or clears the entry and returns false (Go's getCachedHDNEA).</summary>
    public bool TryGet(string cacheKey, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrEmpty(cacheKey) || !_tokens.TryGetValue(cacheKey, out var entry))
        {
            return false;
        }

        if (string.IsNullOrEmpty(entry.Token) || _now() - entry.UpdatedAt > TokenTtl)
        {
            _tokens.TryRemove(cacheKey, out _);
            return false;
        }

        token = entry.Token;
        return true;
    }

    /// <summary>Stores the channel in the dead cache, throttling further recovery attempts.</summary>
    public void MarkDead(string channelId)
    {
        if (!string.IsNullOrEmpty(channelId))
        {
            _dead[channelId] = _now();
        }
    }

    /// <summary>True if this channel 404'd recently; skip expensive recovery polls.</summary>
    public bool IsRecentlyDead(string channelId)
    {
        if (string.IsNullOrEmpty(channelId))
        {
            return false;
        }

        if (!_dead.TryGetValue(channelId, out var fetchedAt))
        {
            return false;
        }

        if (_now() - fetchedAt > DeadChannelTtl)
        {
            _dead.TryRemove(channelId, out _);
            return false;
        }

        return true;
    }

    /// <summary>Clears the dead marker after a successful render.</summary>
    public void ClearDead(string channelId) => _dead.TryRemove(channelId, out _);
}

/// <summary>
/// Rewrites upstream JioTV manifests so every .m3u8/.ts/.aac/.key reference
/// points at our proxy endpoints with AES-encrypted upstream URLs. Port of
/// JioTV Go's RenderHandler rewriting logic + television.ReplaceM3U8/TS/Key.
/// </summary>
public sealed partial class ManifestRewriter
{
    private readonly ISecureUrlCipher _cipher;
    private readonly string _baseEndpoint;
    // Media URI with optional query string (Go pattern kept verbatim).
    [GeneratedRegex("[a-z0-9=_\\-A-Z/\\.]*\\.(m3u8|ts|aac)(\\?[^\\s\"']*)?")]
    private static partial Regex MediaUriRegex();

    // URLs ending in .key/.pkey (Go pattern kept verbatim).
    [GeneratedRegex("http[\\S]+\\.(pkey|key)")]
    private static partial Regex KeyUrlRegex();

    /// <summary>Creates a rewriter; <paramref name="baseEndpoint"/> is e.g. "/JioTv".</summary>
    public ManifestRewriter(ISecureUrlCipher cipher, string baseEndpoint)
    {
        _cipher = cipher;
        _baseEndpoint = baseEndpoint.TrimEnd('/');
    }

    /// <summary>
    /// Fixes a relative manifest path into an absolute URL, mirroring
    /// toAbsoluteStreamURL (handles "//host", "host/path", and "/path" with a
    /// JioTV CDN fallback base).
    /// </summary>
    public static string ToAbsoluteStreamUrl(string streamUrl, string? upstreamBaseUrl)
    {
        if (string.IsNullOrEmpty(streamUrl))
        {
            return string.Empty;
        }

        if (streamUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || streamUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return streamUrl;
        }

        if (streamUrl.StartsWith("//"))
        {
            return "https:" + streamUrl;
        }

        var firstPart = streamUrl.Split('/')[0];
        if (firstPart.Contains('.') && !streamUrl.StartsWith('/'))
        {
            return "https://" + streamUrl;
        }

        if (!streamUrl.StartsWith('/'))
        {
            streamUrl = "/" + streamUrl;
        }

        var baseUrl = string.IsNullOrEmpty(upstreamBaseUrl)
            ? "https://jiotvapi.cdn.jio.com"
            : upstreamBaseUrl;

        return baseUrl.TrimEnd('/') + streamUrl;
    }

    /// <summary>Removes hdnea/__hdnea__ query params (Go's stripHDNEAFromURL).</summary>
    public static string StripHdneaFromUrl(string streamUrl)
    {
        if (string.IsNullOrEmpty(streamUrl) || streamUrl.IndexOf('?') < 0)
        {
            return streamUrl;
        }

        var parts = streamUrl.Split('?', 2);
        var kept = new List<string>();
        foreach (var pair in parts[1].Split('&'))
        {
            var name = pair.Split('=')[0];
            if (name is "hdnea" or "__hdnea__")
            {
                continue;
            }

            kept.Add(pair);
        }

        return kept.Count == 0 ? parts[0] : parts[0] + "?" + string.Join("&", kept);
    }

    /// <summary>
    /// Core rewrite pass. Returns the manifest with every media URL rewritten
    /// to a proxy endpoint carrying the AES-encrypted upstream URL.
    /// </summary>
    /// <param name="manifestBody">Upstream manifest text.</param>
    /// <param name="channelId">Channel id for channel_key_id param.</param>
    /// <param name="upstreamBaseUrl">Resolved absolute URL minus suffix, used as rewrite base.</param>
    /// <param name="upstreamParams">Query params string from upstream URL (may include __hdnea__).</param>
    /// <param name="quality">Optional quality request passed through as q=.</param>
    public string Rewrite(string manifestBody, string channelId, string upstreamBaseUrl, string upstreamParams, string quality)
    {
        var baseStringUrl = upstreamBaseUrl.Split('?')[0];
        var baseDir = Regex.Replace(baseStringUrl, "[a-z0-9=_\\-A-Z\\.]*\\.m3u8", string.Empty);

        var parameters = upstreamParams ?? string.Empty;
        if (parameters.Contains("__hdnea__=", StringComparison.Ordinal))
        {
            // keep as-is: parcel token through to proxy segment requests
        }

        return MediaUriRegex().Replace(manifestBody, match =>
        {
            return MediaUriExtension(match.Value) switch
            {
                ".m3u8" => CreateEncryptedProxyPath(baseDir, match.Value, parameters, channelId, $"{_baseEndpoint}/manifest.m3u8", quality),
                ".ts" => CreateEncryptedProxyPath(baseDir, match.Value, parameters, channelId, $"{_baseEndpoint}/segment.ts"),
                ".aac" => CreateEncryptedProxyPath(baseDir, match.Value, parameters, channelId, $"{_baseEndpoint}/segment.ts"),
                _ => match.Value,
            };
        }).Let(body => KeyUrlRegex().Replace(body, keyMatch =>
            CreateEncryptedProxyPath(string.Empty, keyMatch.Value, parameters, channelId, $"{_baseEndpoint}/key")));
    }

    /// <summary>Strips query string then identifies media extension (Go's mediaURIExtension).</summary>
    public static string MediaUriExtension(string match)
    {
        var path = match;
        var queryIndex = match.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = match[..queryIndex];
        }

        foreach (var ext in new[] { ".m3u8", ".ts", ".aac" })
        {
            if (path.EndsWith(ext, StringComparison.Ordinal))
            {
                return ext;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Builds the proxy path: endpoint?auth={encrypted}&amp;channel_key_id {id} [&amp;q {quality}].
    /// Port of pkg/television CreateEncryptedURL.
    /// </summary>
    public string CreateEncryptedProxyPath(string baseUrl, string match, string @params, string channelId, string endpoint, string? quality = null)
    {
        var fullUrl = baseUrl + match;
        if (!string.IsNullOrEmpty(@params))
        {
            fullUrl += fullUrl.Contains('?') ? "&" : "?";
            fullUrl += @params;
        }

        var encrypted = _cipher.EncryptDeterministic(fullUrl);
        var result = $"{endpoint}?auth={encrypted}";
        if (!string.IsNullOrEmpty(channelId))
        {
            result += $"&channel_key_id={channelId}";
        }

        if (!string.IsNullOrEmpty(quality))
        {
            result += $"&q={quality}";
        }

        return result;
    }
}

/// <summary>
/// Small functional extension used by ManifestRewriter.Rewrite for chaining
/// regex passes; keeps the Go interpreter style of nested ReplaceAllFunc.
/// </summary>
public static class RewriteExtension
{
    /// <summary>Applies a continuation to a value (functional Let).</summary>
    public static TResult Let<TSource, TResult>(this TSource source, Func<TSource, TResult> func)
        => func(source);
}
