using System;
using System.Linq;
using System.Threading.Tasks;
using JioTv.Plugin.Auth;
using JioTv.Plugin.Network;

namespace JioTv.Plugin.Streaming;

/// <summary>
/// Abstraction over the pieces HlsProxyRenderer needs so the decision tree
/// (port of Go's RenderHandler) is testable without real HTTP.
/// </summary>
public interface IUpstream
{
    /// <summary>Fetches an upstream URL with an optional __hdnea__ cookie; returns status, body, and any rotated token.</summary>
    Task<(int Status, string Body, string NewHdnea)> RenderAsync(string url, string hdnea);

    /// <summary>Requests a fresh playback URL for the channel (token harvest source).</summary>
    Task<LiveResult> LiveAsync(string channelId);
}

/// <summary>
/// Result of a manifest render attempt for the HTTP layer to forward.
/// </summary>
public sealed class RenderOutcome
{
    /// <summary>Upstream HTTP status to mirror to the player.</summary>
    public int StatusCode { get; init; }

    /// <summary>Manifest body (already rewritten) or raw bytes for segments.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Fresh __hdnea__ harvested from an upstream response, if any.</summary>
    public string NewHdneaExactly { get; }

    /// <summary>Creates an outcome.</summary>
    public RenderOutcome(int statusCode, string body, string newHdnea = "")
    {
        StatusCode = statusCode;
        Body = body;
        NewHdneaExactly = newHdnea;
    }
}

/// <summary>
/// The streaming core: renders manifests with transparent self-healing.
/// Port of Jellyfin-targeted logic from JioTV Go's internal/handlers
/// RenderHandler (401/403/404 recovery, token harvest, quality ladder,
/// dead-channel throttle).
/// </summary>
public sealed class HlsProxyRenderer
{
    private static readonly string[] QualityCandidates = ["auto", "high", "medium", "low"];

    private readonly IUpstream _upstream;
    private readonly HdneaCache _cache;
    private readonly ManifestRewriter _rewriter;

    /// <summary>Creates a renderer over the given upstream adapter.</summary>
    public HlsProxyRenderer(IUpstream upstream, HdneaCache cache, ManifestRewriter rewriter)
    {
        _upstream = upstream;
        _cache = cache;
        _rewriter = rewriter;
    }

    /// <summary>
    /// Renders a manifest: fetch upstream, rewrite through the proxy; on
    /// 401/403/404 re-request Live, harvest fresh token, retry, and if still
    /// 404 walk the quality ladder before throttling via dead cache.
    /// </summary>
    public async Task<RenderOutcome> RenderManifestAsync(string channelId, string upstreamUrl, string qa)
    {
        // token preference: URL token first; cache overrides when fresher
        _ = _cache.TryGet(HdneaCache.CacheKey(channelId, upstreamUrl), out var cachedToken);
        var hasCached = string.IsNullOrEmpty(cachedToken);
        var urlToken = JioTvClient.TryExtractHdneaFromUrl(upstreamUrl, out var urlTok) ? urlTok : string.Empty;
        var token = !hasCached ? cachedToken! : urlToken;
        var renderUrl = !hasCached ? ManifestRewriter.StripHdneaFromUrl(upstreamUrl) : upstreamUrl;

        var (statusCode, body, newHdnea) = await _upstream.RenderAsync(renderUrl, token).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(newHdnea))
        {
            _cache.Set(channelId, newHdnea);
            token = newHdnea;
        }

        if (statusCode != 401 && statusCode != 403 && statusCode != 404)
        {
            if (statusCode == 200)
            {
                _cache.ClearDead(channelId);
                body = _rewriter.Rewrite(body, channelId, ManifestRewriter.StripHdneaFromUrl(upstreamUrl), ExtractParams(upstreamUrl), qa);
            }

            return new RenderOutcome(statusCode, body, token);
        }

        // ---- recovery path (401/403/404) ----
        if (statusCode != 404)
        {
            _cache.TryGet(channelId, out _);
        }

        if (statusCode == 404 && _cache.IsRecentlyDead(channelId))
        {
            // Expensive recovery ran within the TTL window already; let the
            // fresh 404 propagate (mirrors Go's throttle guard).
            return new RenderOutcome(statusCode, body, token);
        }

        LiveResult? refreshed = null;
        try
        {
            refreshed = await _upstream.LiveAsync(channelId).ConfigureAwait(false);
        }
        catch (ExternalApiException)
        {
            // fall through: refresh failure leaves token as-is and we retry once
        }

        if (refreshed is not null)
        {
            if (!string.IsNullOrEmpty(refreshed.Hdnea))
            {
                _cache.Set(HdneaCache.CacheKey(channelId, upstreamUrl), refreshed.Hdnea);
                token = refreshed.Hdnea;
            }

            // Retry preserving the timeline: original URL minus expired token
            var retryUrl = ManifestRewriter.StripHdneaFromUrl(upstreamUrl);
            (statusCode, body, newHdnea) = await _upstream.RenderAsync(retryUrl, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(newHdnea))
            {
                _cache.Set(HdneaCache.CacheKey(channelId, upstreamUrl), newHdnea);
                token = newHdnea;
            }

            // Still 404: walk candidate qualities from the freshly fetched Live output
            if (statusCode == 404)
            {
                var tried = new System.Collections.Generic.HashSet<string> { retryUrl };
                var candidates = new[] { qa }.Concat(QualityCandidates);
                foreach (var candidateQuality in candidates)
                {
                    var candidateRaw = JioTvClient.SelectQuality(candidateQuality, refreshed.Bitrates.Auto, refreshed.Bitrates.High, refreshed.Bitrates.Medium, refreshed.Bitrates.Low);
                    if (string.IsNullOrEmpty(candidateRaw))
                    {
                        continue;
                    }

                    var candidateUrl = ManifestRewriter.ToAbsoluteStreamUrl(candidateRaw, refreshed.Result);
                    if (!tried.Add(candidateUrl))
                    {
                        continue;
                    }

                    (statusCode, body, newHdnea) = await _upstream.RenderAsync(candidateUrl, token).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(newHdnea))
                    {
                        _cache.Set(HdneaCache.CacheKey(channelId, upstreamUrl), newHdnea);
                        token = newHdnea;
                    }

                    if (statusCode == 200)
                    {
                        break;
                    }
                }
            }
        }

        if (statusCode == 404)
        {
            _cache.MarkDead(channelId);
        }
        else if (statusCode == 200)
        {
            _cache.ClearDead(channelId);
        }

        if (statusCode == 200)
        {
            var baseForRewrite = upstreamUrl;
            body = _rewriter.Rewrite(body, channelId, baseForRewrite, ExtractParams(upstreamUrl), qa);
        }

        return new RenderOutcome(statusCode, body, token);
    }

    private static string ExtractParams(string url)
    {
        var cut = url.IndexOf('?');
        return cut < 0 ? string.Empty : url[(cut + 1)..];
    }
}
