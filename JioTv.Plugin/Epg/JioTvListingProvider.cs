using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JioTv.Plugin.Auth;
using JioTv.Plugin.Network;
using JioTv.Plugin.Tuning;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

#pragma warning disable CS1591

namespace JioTv.Plugin.Epg;

/// <summary>
/// A Jellyfin ListingsProvider backed entirely by Jio's own EPG endpoint.
/// EPG is fetched <c>lazily per channel</c> (the day-0 and day-1 entries only),
/// so the guide never blocks on a giant bulk download — a single guide-map
/// request for one channel costs at most two Jio calls.
/// </summary>
public sealed class JioTvListingProvider : IListingsProvider
{
    private readonly JioTvClient _client;
    private readonly Func<string, Task<(int Status, string Body)>> _fetcher;

    private const string LineupId = "jiotv-listings";
    private const string TunerDeviceId = "jiotv-tuner-1";

    /// <summary>Day-offsets fetched for each requested channel.</summary>
    private static readonly int[] DayOffsets = [0, 1];

    /// <summary>Per-(channel, offset) programme cache; value is a task so concurrent
    /// guide requests for the same channel share one Jio round trip each.</summary>
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, Task<List<JioEpgEntry>>>> _epgByChannel = new();

    /// <summary>Date-stamp of the in-memory cache; programme rows roll over daily.</summary>
    private volatile string _cacheDay = string.Empty;

    public JioTvListingProvider(
        JioTvClient client,
        Func<string, Task<(int Status, string Body)>> fetcher)
    {
        _client = client;
        _fetcher = fetcher;
    }

    public string Name => "JioTV";

    public string Type => "jiotv";

    /// <inheritdoc />
    public async Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var channels = await _client.GetChannelsAsync().ConfigureAwait(false);
        var mapped = JioTvChannelMapper.Map(channels, TunerDeviceId).ToList();
        return mapped.Select(c => new ChannelInfo
        {
            Id = c.Id,
            Name = c.Name,
            ImageUrl = c.ImagePath,
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        ListingsProviderInfo info,
        string channelId,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(channelId))
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!int.TryParse(channelId, System.Globalization.CultureInfo.InvariantCulture, out var channelNum))
        {
            return [];
        }

        var entries = new List<JioEpgEntry>();
        foreach (var offset in DayOffsets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var bucket = await GetOrStartAsync(channelNum, offset, cancellationToken).ConfigureAwait(false);
                entries.AddRange(bucket);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Skip un-fetchable channel/day but let the rest of the guide proceed.
            }
        }

        var today = System.DateTime.UtcNow.Date;
        _cacheDay = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var result = new List<ProgramInfo>();
        foreach (var entry in entries)
        {
            var start = System.DateTimeOffset.FromUnixTimeMilliseconds(entry.StartEpoch).UtcDateTime;
            var stop = System.DateTimeOffset.FromUnixTimeMilliseconds(entry.EndEpoch).UtcDateTime;
            if (stop <= startDateUtc.ToUniversalTime() || start >= endDateUtc.ToUniversalTime())
            {
                continue;
            }

            result.Add(new ProgramInfo
            {
                Id = $"{channelId}_{start:yyyyMMddHHmmss}",
                ChannelId = channelId,
                Name = string.IsNullOrWhiteSpace(entry.Title) ? string.Empty : entry.Title,
                Overview = entry.Desc,
                StartDate = start,
                EndDate = stop,
                Genres = new List<string> { entry.Category },
                IsHD = false,
                IsRepeat = false,
                IsLive = true,
                IsKids = false,
                IsPremiere = false,
                HasImage = !string.IsNullOrWhiteSpace(entry.Poster),
            });
        }

        return result;
    }

    /// <summary>Gets or starts the per-(channel, offset) fetch. Retries on the next guide run after failure.</summary>
    private Task<List<JioEpgEntry>> GetOrStartAsync(int channelNum, int offset, CancellationToken cancellationToken)
    {
        var channelMap = _epgByChannel.GetOrAdd(channelNum, _ => new ConcurrentDictionary<int, Task<List<JioEpgEntry>>>());
        var task = channelMap.GetOrAdd(
            offset,
            _ => FetchChannelProgrammesAsync(channelNum, offset, cancellationToken));

        if (task.IsFaulted)
        {
            // Forget the failure so the next guide pass retries the fetch.
            channelMap.TryRemove(new KeyValuePair<int, Task<List<JioEpgEntry>>>(offset, task));
        }

        return task;
    }

    /// <summary>Fetches one day-offset for one channel from Jio's getepg endpoint.</summary>
    private async Task<List<JioEpgEntry>> FetchChannelProgrammesAsync(int channelNum, int offset, CancellationToken cancellationToken)
    {
        var url = EpgUrlFormatter.Format(offset, channelNum);
        var (status, body) = await _fetcher(url).ConfigureAwait(false);
        var payload = System.Text.Json.JsonSerializer.Deserialize<JioEpgResponse>(body);
        if (payload?.Epg is null || payload.Epg.Count == 0)
        {
            throw new ExternalApiException($"No EPG programmes for channel {channelNum} (status {status})");
        }

        return payload.Epg;
    }

    /// <inheritdoc />
    public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
        => Task.FromResult(new List<NameIdPair>
        {
            new() { Name = "JioTV lineup", Id = LineupId },
        });
}

/// <summary>Jio getepg response schema (subset).</summary>
public sealed class JioEpgEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("startEpoch")]
    public long StartEpoch { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("endEpoch")]
    public long EndEpoch { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("showname")]
    public string Title { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string Desc { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("showCategory")]
    public string Category { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("episodePoster")]
    public string Poster { get; set; } = string.Empty;
}

/// <summary>Jio getepg response envelope: {"epg": [entries], "channel_id": …}.</summary>
public sealed class JioEpgResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("epg")]
    public List<JioEpgEntry>? Epg { get; set; }
}
