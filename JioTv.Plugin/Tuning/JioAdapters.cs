using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JioTv.Plugin.Auth;
using JioTv.Plugin.Network;
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
        await _auth.EnsureFreshTokensAsync().ConfigureAwait(false);
        return await _client.GetChannelsAsync().ConfigureAwait(false);
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

    public JioTvStreamSource(JioTvClient client, ManifestRewriter rewriter)
    {
        _client = client;
        _rewriter = rewriter;
    }

    /// <inheritdoc/>
    public async Task<MediaBrowser.Model.Dto.MediaSourceInfo> OpenLiveStreamAsync(string channelId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var live = await _client.LiveAsync(channelId).ConfigureAwait(false);
        var upstreamUrl = JioTvClient.SelectQuality("auto", live.Bitrates.Auto, live.Bitrates.High, live.Bitrates.Medium, live.Bitrates.Low);
        if (string.IsNullOrEmpty(upstreamUrl))
        {
            upstreamUrl = live.Result;
        }

        var authPath = _rewriter.CreateEncryptedProxyPath(
            string.Empty, upstreamUrl, string.Empty, channelId, "/JioTv/manifest.m3u8", "auto");
        return new MediaSourceInfo
        {
            Path = authPath,
            Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.Http,
            Container = "hls",
            // Port of M3U tuner's CreateMediaSourceInfo pattern: Jellyfin's
            // standard playback flow expects RequiresOpening so it routes
            // through ILiveStream.Open (our tuner's GetChannelStream) and
            // assigns an OpenToken — the same lifecycle m3u/HDHomeRun use.
            RequiresOpening = true,
            RequiresClosing = true,
            IsInfiniteStream = true,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = false,
            IsRemote = false,
        };
    }
}
