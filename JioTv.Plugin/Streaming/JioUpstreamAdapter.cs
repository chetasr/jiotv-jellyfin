using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using JioTv.Plugin.Auth;
using JioTv.Plugin.Network;

#pragma warning disable CS1591

namespace JioTv.Plugin.Streaming;

/// <summary>
/// Outbound proxy plumbing: fetches a manifest/segment/key upstream with the
/// correct token overrides. Unit testable without ASP.NET; the controller is
/// a thin wrapper over these helpers.
/// </summary>
public interface IJioProxyFetcher
{
    /// <summary>Fetches an upstream resource/body with __hdnea__ cookie and player UA.</summary>
    Task<(int Status, byte[] Body, string NewHdnea)> FetchAsync(string url, string hdnea);
}

/// <summary>Production fetcher backed by the shared <see cref="JioHttp.HttpClient"/>.</summary>
public sealed class JioProxyFetcher : IJioProxyFetcher
{
    /// <summary>Mirrors Go's tv.Render: player UA because some CDNs block okhttp.</summary>
    public async Task<(int Status, byte[] Body, string NewHdnea)> FetchAsync(string url, string hdnea)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", JioConstants.UserAgentPlayTv);
        if (!string.IsNullOrEmpty(hdnea))
        {
            request.Headers.TryAddWithoutValidation("Cookie", "__hdnea__=" + hdnea);
        }

        using var response = await JioHttp.HttpClient.SendAsync(request).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

        var newHdnea = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(CookieHelpers.ParseRotatedHdnea).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? string.Empty
            : string.Empty;

        return ((int)response.StatusCode, data, newHdnea);
    }
}

/// <summary>
/// Live-harvest adapter: lets <see cref="HlsProxyRenderer"/> request a fresh
/// playback URL when it needs to heal a failing stream. Reuses JioTvClient.
/// </summary>
public sealed class JioUpstreamAdapter : IUpstream
{
    /// <summary>Wraps IJioProxyFetcher as the IUpstream HTTP face.</summary>
    private readonly IJioProxyFetcher _fetcher;

    /// <summary>Playback client factory for Live harvest (nullable in tests).</summary>
    private readonly Func<JioTvClient>? _clientFactory;

    public JioUpstreamAdapter(IJioProxyFetcher fetcher, Func<JioTvClient>? clientFactory = null)
    {
        _fetcher = fetcher;
        _clientFactory = clientFactory;
    }

    /// <summary>Treats binary segment data as UTF-8 lossless enough for this abstraction; segments are proxied raw by the segment endpoint.</summary>
    public async Task<(int Status, string Body, string NewHdnea)> RenderAsync(string url, string hdnea)
    {
        var (status, body, newHdnea) = await _fetcher.FetchAsync(url, hdnea).ConfigureAwait(false);
        return (status, System.Text.Encoding.UTF8.GetString(body), newHdnea);
    }

    /// <summary>Hooks Live harvest up when a playback client is available.</summary>
    public Task<LiveResult> LiveAsync(string channelId)
    {
        if (_clientFactory is null)
        {
            throw new ExternalApiException("No playback client configured for live harvest");
        }

        return _clientFactory().LiveAsync(channelId);
    }
}
