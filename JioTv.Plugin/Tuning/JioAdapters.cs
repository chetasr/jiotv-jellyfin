using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using JioTv.Plugin.Auth;
using JioTv.Plugin.Network;
using Microsoft.AspNetCore.Http;
using JioTv.Plugin.Streaming;
using MediaBrowser.Model.Dto;

#pragma warning disable CS1591

namespace JioTv.Plugin.Tuning;

/// <summary>
/// Default channel enumeration source using JioTvClient + JioAuth.
/// </summary>
public sealed class JioTvChannelSource : IJioChannels
{
    private readonly JioTvClient _client;
    private readonly JioAuth _auth;

    public JioTvChannelSource(JioTvClient client, JioAuth auth)
    {
        _client = client;
        _auth = auth;
    }

    /// <inheritdoc/>
    public async Task<List<Channel>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _auth.EnsureFreshTokensAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // No credentials stored: channel listing works without them, and a
            // failed auth check must not break tuner enumeration or the guide.
        }
        var list = await _client.GetChannelsAsync().ConfigureAwait(false);
        list.AddRange(SonyChannels.ToJioChannelList());
        return list;
    }
}

/// <summary>
/// Stream-opening source: fetches a fresh playback URL and produces a
/// MediaSourceInfo whose Path is a plugin-local proxy URL
/// (fully opaque; self-healing happens inside the controller).
/// </summary>
public sealed class JioTvStreamSource : IJioStreams
{
    private readonly JioTvClient _client;
    private readonly ManifestRewriter _rewriter;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly MediaBrowser.Controller.IServerApplicationHost? _appHost;

    public JioTvStreamSource(JioTvClient client, ManifestRewriter rewriter, IHttpContextAccessor? httpContextAccessor = null, MediaBrowser.Controller.IServerApplicationHost? appHost = null)
    {
        _client = client;
        _rewriter = rewriter;
        _httpContextAccessor = httpContextAccessor;
        _appHost = appHost;
    }

    /// <inheritdoc/>
    public async Task<MediaBrowser.Model.Dto.MediaSourceInfo> OpenLiveStreamAsync(string channelId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string upstreamUrl;
        if (SonyChannels.IsSony(channelId))
        {
            // SonyLIV channels resolve through the Google DAI redirect chain,
            // not our Jio playback API (port of JioTV Go's getSLChannel).
            upstreamUrl = await SonyChannels.ResolveSonyLiveUrlAsync(channelId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var live = await _client.LiveAsync(channelId).ConfigureAwait(false);
            upstreamUrl = JioTvClient.SelectQuality("auto", live.Bitrates.Auto, live.Bitrates.High, live.Bitrates.Medium, live.Bitrates.Low);
            if (string.IsNullOrEmpty(upstreamUrl))
            {
                upstreamUrl = live.Result;
            }
        }

        var authPath = _rewriter.CreateEncryptedProxyPath(
            string.Empty, upstreamUrl, string.Empty, channelId, "/JioTv/manifest.m3u8", "auto");
        var absolutePath = MakeAbsolute(authPath);
        var mediaSource = new MediaSourceInfo
        {
            Path = absolutePath,
            Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.Http,
            Container = "hls",
            // Port of M3U tuner's CreateMediaSourceInfo pattern: Jellyfin's
            // standard playback flow expects RequiresOpening so it routes
            // through ILiveStream.Open (our tuner's GetChannelStream) and
            // assigns an OpenToken — the same lifecycle m3u/HDHomeRun use.
            // The Path MUST be absolute: Jellyfin probes/plays HTTP sources
            // by fetching Path verbatim, and relative paths fail (10.11.
            // MediaSourceManager: "Error probing live tv stream").
            RequiresOpening = true,
            RequiresClosing = true,
            IsInfiniteStream = true,
            BufferMs = 3000,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            // Allow Jellyfin's ffmpeg remux for clients without HLS direct-play
            // (UHF, third-party apps, etc.); direct-play stays the preference.
            SupportsTranscoding = true,
            // HLS proxy: Jellyfin's ffprobe of our own URL can be unreachable
            // inside containers (mapped-port mismatch) and adds nothing for
            // our self-describing manifest — skip probing entirely.
            SupportsProbing = false,
            IsRemote = false,
        };
        mediaSource.MediaStreams = [VideoStream(), AudioStream()];
        return mediaSource;
    }

    /// <summary>Static stream descriptors for Broadpeak-packaged Jio HLS
    /// (always h.264 + AAC at all qualities). Telling the player the codecs
    /// up-front lets direct-play win without any ffprobe round trip.</summary>
    private static MediaBrowser.Model.Entities.MediaStream VideoStream() => new()
    {
        Type = MediaBrowser.Model.Entities.MediaStreamType.Video,
        Codec = "h264",
        Index = -1,
        IsInterlaced = true,
        RealFrameRate = 25,
        BitRate = 2_000_000,
    };

    private static MediaBrowser.Model.Entities.MediaStream AudioStream() => new()
    {
        Type = MediaBrowser.Model.Entities.MediaStreamType.Audio,
        Codec = "aac",
        Index = -1,
        Channels = 2,
        SampleRate = 48_000,
    };

    /// <summary>
    /// Makes the relative proxy path absolute so players and Jellyfin's own
    /// MediaSourceManager can fetch it. The LiveStream-Open path runs inside
    /// the HTTP request pipeline, so the request context gives us the
    /// client-visible address (scheme://host[:port] honouring reverse proxies
    /// and https); fallback is the smart bind address.
    /// </summary>
    private string MakeAbsolute(string relativePath)
    {
        var host = _httpContextAccessor is null ? null : _httpContextAccessor.HttpContext;
        if (host?.Request.Host.Value is { Length: > 0 } hostname)
        {
            return string.Concat(host.Request.Scheme, "://", hostname, relativePath);
        }

        if (_appHost is null)
        {
            // Test/edge path: keep relative (same as pre-0.1.4 behaviour).
            return relativePath;
        }

        var remoteIp = host?.Connection.RemoteIpAddress;
        if (remoteIp is not null)
        {
            return _appHost.GetSmartApiUrl(remoteIp) + relativePath;
        }

        return _appHost.GetSmartApiUrl(IPAddress.Loopback) + relativePath;
    }
}
