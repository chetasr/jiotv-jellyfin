using System;
using System.Collections.Generic;
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
    /// <summary>
    /// Fetches an upstream resource with Jio impersonation headers.
    /// <paramref name="isKey"/> upgrades the header set to the full Jio app
    /// headers (appkey/ssotoken/srno/channelId) that the AES-key endpoint
    /// demands; segments only need player UA + the hdnea cookie.
    /// </summary>
    Task<(int Status, byte[] Body, string NewHdnea)> FetchAsync(string url, string channelId, string? cookie, bool isKey);
}

/// <summary>Production fetcher backed by the shared <see cref="JioHttp.HttpClient"/>.</summary>
public sealed class JioProxyFetcher : IJioProxyFetcher
{
    private readonly CredentialStore _store;

    public JioProxyFetcher(CredentialStore store)
    {
        _store = store;
    }

    /// <summary>Mirrors Go's tv.Render headers (Television.New) + RenderKeyHandler header set.</summary>
    private Dictionary<string, string> BuildHeaders(JioCredentials creds, string channelId, bool isKey)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["appkey"] = "NzNiMDhlYzQyNjJm",
            ["deviceId"] = creds.DeviceId,
            ["devicetype"] = "phone",
            ["isott"] = "false",
            ["languageId"] = "6",
            ["lbcookie"] = "1",
            ["os"] = JioConstants.OsAndroid,
            ["osVersion"] = "13",
            ["subscriberId"] = creds.CRM,
            ["userId"] = creds.CRM,
            ["uniqueId"] = creds.UniqueId,
            ["useragent"] = JioConstants.UserAgentOkHttp,
            ["usergroup"] = JioConstants.UserGroup,
            ["versionCode"] = JioConstants.VersionCode,
        };

        if (isKey)
        {
            // RenderKeyHandler adds the key-endpoint-specific app headers.
            headers["srno"] = "230203144000";
            headers["ssotoken"] = creds.SSOToken;
            headers["channelId"] = channelId;
        }

        return headers;
    }

    /// <summary>Mimics Go's SetPlayerHeaders: strips browser style headers.</summary>
    private static HttpRequestMessage ApplyHeaders(HttpRequestMessage request, Dictionary<string, string> headers, string? cookie, bool isKey)
    {
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Headers.TryAddWithoutValidation("User-Agent", JioConstants.UserAgentPlayTv);
        request.Headers.TryAddWithoutValidation("Connection", "keep-alive");

        // Browser style headers must not leak upstream.
        request.Headers.Remove("Accept");
        request.Headers.Remove("Accept-Encoding");
        request.Headers.Remove("Accept-Language");
        request.Headers.Remove("Origin");
        request.Headers.Remove("Referer");
        request.Headers.Remove("Authorization");

        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        return request;
    }

    /// <inheritdoc/>
    public async Task<(int Status, byte[] Body, string NewHdnea)> FetchAsync(string url, string channelId, string? cookie, bool isKey)
    {
        var creds = _store.Load();
        if (creds is null || string.IsNullOrEmpty(creds.AccessToken))
        {
            throw new ExternalApiException("No credentials stored; cannot proxy stream");
        }

        var headers = BuildHeaders(creds, channelId, isKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        _ = ApplyHeaders(request, headers, cookie ?? string.Empty, isKey);

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
    private readonly IJioProxyFetcher _fetcher;
    private readonly Func<JioTvClient>? _clientFactory;

    public JioUpstreamAdapter(IJioProxyFetcher fetcher, Func<JioTvClient>? clientFactory = null)
    {
        _fetcher = fetcher;
        _clientFactory = clientFactory;
    }

    /// <summary>Treats binary segment data as UTF-8 lossless enough for this abstraction; segments are proxied raw by the segment endpoint.</summary>
    public async Task<(int Status, string Body, string NewHdnea)> RenderAsync(string url, string hdnea)
    {
        var (status, body, newHdnea) = await _fetcher.FetchAsync(url, channelId: string.Empty, string.IsNullOrEmpty(hdnea) ? string.Empty : "__hdnea__=" + hdnea, isKey: false).ConfigureAwait(false);
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
