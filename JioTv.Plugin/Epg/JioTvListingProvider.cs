using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JioTv.Plugin.Epg;
using JioTv.Plugin.Network;
using JioTv.Plugin.Tuning;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Serialization;

#pragma warning disable CS1591

namespace JioTv.Plugin.Epg;

/// <summary>
/// A Jellyfin ListingsProvider backed entirely by Jio's own EPG endpoint.
/// Port of the raw XML output of JioTV Go's epg pipeline — consumed by
/// Jellyfin's ListingsManager just like an XMLTV source.
/// </summary>
public sealed class JioTvListingProvider : IListingsProvider
{
    private readonly JioTvClient _client;
    private readonly JioEpgGenerator _generator;
    private readonly Lazy<EpgCacheService> _cache;

    private const string LineupId = "jiotv-listings";

    private readonly string _epgDir;

    public JioTvListingProvider(JioTvClient client, JioEpgGenerator generator, IApplicationPaths appPaths)
    {
        _epgDir = System.IO.Path.Combine(appPaths.CachePath, "JioTv");
        System.IO.Directory.CreateDirectory(_epgDir);
        _client = client;
        _generator = generator;
        _cache = new Lazy<EpgCacheService>(() =>
            new EpgCacheService(System.IO.Path.Combine(_epgDir, "epg.xml.gz")));
    }

    public string Name => "JioTV";

    public string Type => "jiotv";

    /// <summary>Cached XML document after the first generate pass.</summary>
    private TvDocument? _document;

    /// <summary>Loads (and regenerates if the cache is stale) the parsed EPG document.</summary>
    private async Task<TvDocument> EnsureDocumentAsync()
    {
        if (_document is not null)
        {
            return _document;
        }

        await _cache.Value.GetOrCreateAsync(async () => await _generator.GenerateAsync().ConfigureAwait(false))
            .ConfigureAwait(false);

        var path = System.IO.Path.Combine(_epgDir, "epg.xml.gz");
        var xml = EpgModel.GzipRead(path);
        var settings = new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Ignore,
            XmlResolver = null,
        };
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(TvDocument));
        using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xml), settings);
        _document = (TvDocument?)serializer.Deserialize(reader) ?? throw new InvalidOperationException("EPG could not be parsed");
        return _document;
    }

    /// <inheritdoc />
    public async Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var channels = await _client.GetChannelsAsync().ConfigureAwait(false);
        var mapped = JioTvChannelMapper.Map(channels, "jiotv-tuner-1").ToList();
        var list = mapped.Select(c => new ChannelInfo
        {
            Id = c.Id,
            Name = c.Name,
            ImageUrl = c.ImagePath,
        }).ToList();
        return list;
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

        var document = await EnsureDocumentAsync().ConfigureAwait(false);
        var programmes = document.Programmes
            .Where(p => string.Equals(p.Channel, channelId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (programmes.Count == 0)
        {
            return [];
        }

        var result = new List<ProgramInfo>();
        var baseName = document.Channels
            .FirstOrDefault(c => c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == channelId)?
            .Display ?? string.Empty;
        foreach (var p in programmes)
        {
            var start = ParseXmltvDate(p.Start);
            var stop = ParseXmltvDate(p.Stop);
            if (start is null || stop is null)
            {
                continue;
            }

            if (stop.Value <= startDateUtc.ToUniversalTime() || start.Value >= endDateUtc.ToUniversalTime())
            {
                continue;
            }

            result.Add(new ProgramInfo
            {
                Id = $"{channelId}_{start:yyyyMMddHHmmss}",
                ChannelId = channelId,
                Name = string.IsNullOrWhiteSpace(p.Title) ? baseName : p.Title,
                Overview = p.Desc,
                StartDate = start.Value,
                EndDate = stop.Value,
                Genres = new List<string> { p.Category },
                IsHD = false,
                IsRepeat = false,
                IsKids = false,
                IsLive = true,
                IsPremiere = false,
                HasImage = false,
            });
        }

        return result;
    }

    private static DateTime? ParseXmltvDate(string v)
    {
        // yyyyMMddHHmmss zzz
        if (string.IsNullOrEmpty(v) || v.Length < 15)
        {
            return null;
        }

        if (!DateTime.TryParseExact(
                v,
                new[] { "yyyyMMddHHmmss zzz", "yyyyMMddHHmmss", "yyyyMMddHHmmss +0000" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }

        return parsed.ToUniversalTime();
    }

    /// <inheritdoc />
    public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
        => Task.FromResult(new List<NameIdPair>
        {
            new NameIdPair { Name = "JioTV lineup", Id = LineupId },
        });
}
