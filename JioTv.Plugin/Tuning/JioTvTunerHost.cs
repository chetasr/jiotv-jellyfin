using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JioTv.Plugin.Network;
using JioTv.Plugin.Streaming;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Session;

#pragma warning disable CS1591

namespace JioTv.Plugin.Tuning;

/// <summary>
/// Jellyfin TunerHost streaming JioTV channels. Channel enumeration comes
/// from the JioTV API; playback opens an <see cref="ILiveStream"/> pointing
/// at the plugin's own proxy endpoints, which carry token auth transparently.
/// </summary>
public class JioTvTunerHost : ITunerHost
{
    private const string TunerId = "jiotv-tuner-1";

    private readonly IJioChannels _channelSource;
    private readonly IJioStreams _streamSource;

    public JioTvTunerHost(IJioChannels channelSource, IJioStreams streamSource)
    {
        _channelSource = channelSource;
        _streamSource = streamSource;
    }

    public string Name => "JioTV";
    public string Type => "jiotv";
    public bool IsSupported => true;

    /// <summary>Enumerates channels (channel_id, name, logo, numeric ordering).</summary>
    public async Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
    {
        System.Console.WriteLine("### JIO-TUNER: GetChannels called, enableCache=" + enableCache);
        var channels = await _channelSource.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        System.Console.WriteLine("### JIO-TUNER: upstream channel count=" + channels.Count);
        return JioTvChannelMapper.Map(channels, TunerId).ToList();
    }

    /// <summary>Opens a live stream for the channel, resolving a fresh manifest URL.</summary>
    public async Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        System.Console.WriteLine("### JIO-TUNER: GetChannelStream(" + channelId + "," + streamId + ")");
        var mediaSource = await _streamSource.OpenLiveStreamAsync(channelId, cancellationToken).ConfigureAwait(false);
        return new JioTvLiveStream(mediaSource);
    }

    /// <summary>Jellyfin calls this to decide what a channel can offer; v1 offers one HLS source.</summary>
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        System.Console.WriteLine("### JIO-TUNER: GetChannelStreamMediaSources(" + channelId + ")");
        var mediaSource = await _streamSource.OpenLiveStreamAsync(channelId, cancellationToken).ConfigureAwait(false);
        return [mediaSource];
    }

    /// <summary>JioTV needs no M3U/UPnP style device discovery; still, return empty list.</summary>
    public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken cancellationToken)
        => Task.FromResult(new List<TunerHostInfo>());
}

/// <summary>Adapter interface for channel enumeration (allows test doubles).</summary>
public interface IJioChannels
{
    /// <summary>Returns Jio channel list.</summary>
    Task<List<Channel>> GetChannelsAsync(CancellationToken cancellationToken);
}

/// <summary>Adapter interface for opening per-channel playback (allows test doubles).</summary>
public interface IJioStreams
{
    /// <summary>Resolves and validates a playable MediaSourceInfo for the channel.</summary>
    Task<MediaSourceInfo> OpenLiveStreamAsync(string channelId, CancellationToken cancellationToken);
}

/// <summary>
/// ILiveStream returning a URL-based HLS MediaSourceInfo pointing at the
/// plugin's own proxy endpoint (/JioTv/manifest.m3u8?auth=...) whose ASP.NET
/// controller endpoint performs the actual self-healing fetch upstream.
/// </summary>
public sealed class JioTvLiveStream : ILiveStream, IDisposable
{
    private readonly string _proxyUrl;

    public JioTvLiveStream(MediaSourceInfo mediaSource)
    {
        MediaSource = mediaSource;
        _proxyUrl = mediaSource.Path;
    }

    /// <inheritdoc/>
    public int ConsumerCount { get; set; } = 1;

    /// <inheritdoc/>
    public string OriginalStreamId { get; set; } = "jiotv";

    /// <inheritdoc/>
    public string TunerHostId => "jiotv-tuner-1";

    /// <inheritdoc/>
    public bool EnableStreamSharing { get; set; } = true;

    /// <inheritdoc/>
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc/>
    public string UniqueId => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Opens the stream. For URL-based HLS, no extra work is needed; playback streams from the proxy.</summary>
    public Task Open(CancellationToken openCancellationToken) => Task.CompletedTask;

    /// <summary>Closes the stream — nothing to release for a URL-based HLS source.</summary>
    public Task Close() => Task.CompletedTask;

    /// <summary>Returns null stream; Jellyfin consumes via MediaSource.Path for URL sources.</summary>
    public Stream GetStream() => throw new NotSupportedException("URL-based HLS streams are consumed via MediaSource.Path");

    /// <summary>Releases underlying resources; URL-based HLS needs none.</summary>
    public void Dispose()
    {
        // No stream resources — nothing to dispose for a URL-based HLS source.
        GC.SuppressFinalize(this);
    }
}
