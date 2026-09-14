using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using JioTv.Plugin.Network;
using JioTv.Plugin.Streaming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

#pragma warning disable CS1591

namespace JioTv.Plugin.Streaming;

/// <summary>
/// ASP.NET endpoints for rewritten URLs: /JioTv/manifest.m3u8,
/// /JioTv/segment.ts, /JioTv/key. Decrypts the AES 'auth' payload back to the
/// upstream URL, heals via <see cref="HlsProxyRenderer"/> (manifests) and
/// proxies segments/keys nearly raw with token refresh.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class JioTvProxyController : ControllerBase
{
    private readonly System.IServiceProvider _serviceProvider;
    private readonly ISecureUrlCipher _cipher;
    private readonly HlsProxyRenderer _renderer;
    private readonly IJioProxyFetcher _fetcher;
    private readonly HdneaCache _cache;

    public JioTvProxyController(
        System.IServiceProvider serviceProvider,
        ISecureUrlCipher cipher,
        HlsProxyRenderer renderer,
        IJioProxyFetcher fetcher,
        HdneaCache cache)
    {
        _serviceProvider = serviceProvider;
        _cipher = cipher;
        _renderer = renderer;
        _fetcher = fetcher;
        _cache = cache;
    }

    /// <summary>HLS manifest endpoint: self-heals through the renderer.</summary>
    [HttpGet("/JioTv/whoami")]
    public IActionResult WhoAmI()
    {
        var cipherType = _serviceProvider.GetService(typeof(ISecureUrlCipher))?.GetType().FullName ?? "null";
        var tuners = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetServices<MediaBrowser.Controller.LiveTv.ITunerHost>(_serviceProvider)
            .Select(t => t.GetType().FullName).ToList();
        return Ok(new { cipher = cipherType, appHost = "v1", tuners });
    }

    [HttpGet("/JioTv/manifest.m3u8")]
    public async Task<IActionResult> Manifest(
        [FromQuery(Name = "auth")] string authParam,
        [FromQuery(Name = "channel_key_id")] string channelId,
        [FromQuery(Name = "q")] string quality)
    {
        var upstreamUrl = _cipher.Decrypt(WebUtility.UrlDecode(authParam));
        var outcome = await _renderer.RenderManifestAsync(channelId ?? string.Empty, upstreamUrl, quality ?? "auto").ConfigureAwait(false);

        Response.Headers["Cache-Control"] = "no-store, must-revalidate, max-age=3";
        return StatusCodeWithBody(outcome.StatusCode, outcome.Body, "application/vnd.apple.mpegurl");
    }

    /// <summary>Media segment / key endpoint: streams raw bytes, rotating tokens on auth changes.</summary>
    [HttpGet("/JioTv/segment.ts")]
    [HttpGet("/JioTv/key")]
    public async Task<IActionResult> Segment(
        [FromQuery(Name = "auth")] string authParam,
        [FromQuery(Name = "channel_key_id")] string channelId,
        [FromQuery(Name = "q")] string quality)
    {
        var upstreamUrl = _cipher.Decrypt(WebUtility.UrlDecode(authParam));
        var key = HdneaCache.CacheKey(channelId ?? string.Empty, upstreamUrl);

        if (!_cache.TryGet(key, out var token))
        {
            _ = JioTvClient.TryExtractHdneaFromUrl(upstreamUrl, out var urlToken);
            token = urlToken;
        }

        var (status, body, newHdnea) = await _fetcher.FetchAsync(upstreamUrl, token).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(newHdnea))
        {
            _cache.Set(key, newHdnea);
        }

        Response.Headers["Cache-Control"] = "no-store, must-revalidate";
        return StatusCodeWithBytes(status, body, upstreamUrl.EndsWith(".ts", StringComparison.Ordinal) ? "video/mp2t" : "application/octet-stream");
    }

    private IActionResult StatusCodeWithBody(int statusCode, string body, string contentType)
    {
        return new ContentResult
        {
            StatusCode = statusCode,
            Content = body,
            ContentType = contentType,
        };
    }

    private IActionResult StatusCodeWithBytes(int statusCode, byte[] body, string contentType)
    {
        if (statusCode == 200)
        {
            return new FileContentResult(body, contentType);
        }
        return new ObjectResult(body) { StatusCode = statusCode, ContentTypes = { contentType } };
    }
}
