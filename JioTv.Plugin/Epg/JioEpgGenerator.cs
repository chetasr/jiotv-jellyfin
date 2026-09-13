using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using JioTv.Plugin.Auth;
using JioTv.Plugin.Network;

#pragma warning disable CS1591

namespace JioTv.Plugin.Epg;

/// <summary>Pure EPG data model + XML builder. Port of JioTV Go's pkg/epg.</summary>
public static class EpgModel
{
    /// <summary>JioTV Go's EPG poster base URL, prefixed onto each icon src.</summary>
    public const string EpgPosterUrl = "https://jiotv.catchup.cdn.jio.com/dare_images/shows";

        /// <summary>Builds the full &lt;tv&gt; string from channels + programmes.</summary>
    public static string GenXml(IEnumerable<EpgChannel> channels, IEnumerable<EpgProgramme> programmes)
    {
        var document = new TvDocument
        {
            Channels = channels.ToList(),
            Programmes = programmes.ToList(),
        };
        var serializer = new XmlSerializer(typeof(TvDocument));
        using var memoryStream = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(memoryStream, new System.Xml.XmlWriterSettings
        {
            Indent = false,
            Encoding = new System.Text.UTF8Encoding(false),
            OmitXmlDeclaration = false,
            NewLineChars = string.Empty,
            ConformanceLevel = System.Xml.ConformanceLevel.Document,
        }))
        {
            serializer.Serialize(writer, document);
        }

        return System.Text.Encoding.UTF8.GetString(memoryStream.ToArray()).Trim();
    }

    /// <summary>Creates a Programme entry, prefixing the poster URL base where given.</summary>
    public static EpgProgramme NewProgramme(int channelId, string start, string stop, string title, string desc, string category, string iconSrc)
    {
        var iconUrl = $"{EpgPosterUrl}/{iconSrc}";
        return new EpgProgramme
        {
            Channel = channelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Start = start,
            Stop = stop,
            Title = title,
            Desc = desc,
            Category = category,
            IconSrc = iconSrc.Length > 0 ? iconUrl : string.Empty,
        };
    }

    /// <summary>Formats the timestamp the way Go does: yyyyMMddHHmmss [+-]zzzz (XMLTV convention).</summary>
    public static string FormatTime(DateTimeOffset t)
        => t.ToString("yyyyMMddHHmmss zzz", System.Globalization.CultureInfo.InvariantCulture)
            .Replace(":", string.Empty);

    /// <summary>Compresses an EPG document into epg.xml.gz disk layout.</summary>
    public static void GzipWrite(string xml, string path)
    {
        using var stream = File.Create(path);
        using var gz = new GZipStream(stream, CompressionLevel.Optimal);
        using (var writer = new StreamWriter(gz))
        {
            writer.Write(xml);
        }
    }

    /// <summary>Reads back a gzipped EPG file.</summary>
    public static string GzipRead(string path)
    {
        using var stream = File.OpenRead(path);
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        return reader.ReadToEnd();
    }
}

/// <summary>jio's channel+EPG envelope document (Go EPG struct: &lt;tv&gt;).</summary>
[XmlRoot("tv", Namespace = "")]
public sealed class TvDocument
{
    /// <summary>XML version, always 1.0.</summary>
    [XmlAttribute("version")]
    public string Version { get; set; } = "1.0";

    [XmlElement("channel")]
    public List<EpgChannel> Channels { get; set; } = new();

    [XmlElement("programme")]
    public List<EpgProgramme> Programmes { get; set; } = new();
}

/// <summary>&lt;channel&gt; XML tag from pkg/epg/types.go.</summary>
public sealed class EpgChannel
{
    [XmlAttribute("id")]
    public int Id { get; set; }

    [XmlElement("display-name")]
    public string Display { get; set; } = string.Empty;
}

/// <summary>&lt;programme&gt; XML tag from pkg/epg/types.go.</summary>
public sealed class EpgProgramme
{
    [XmlAttribute("channel")]
    public string Channel { get; set; } = string.Empty;

    [XmlAttribute("start")]
    public string Start { get; set; } = string.Empty;

    [XmlAttribute("stop")]
    public string Stop { get; set; } = string.Empty;

    [XmlElement("title")]
    public string Title { get; set; } = string.Empty;

    [XmlElement("desc")]
    public string Desc { get; set; } = string.Empty;

    [XmlElement("category")]
    public string Category { get; set; } = string.Empty;

    [XmlElement("icon")]
    public string IconSrc { get; set; } = string.Empty;
}

/// <summary>
/// Fetches JioTV EPG from the getepg API for every channel (2 days of
/// look-ahead), builds an SVG-free EPG XML document, and writes it as a
/// gzipped file for Jellyfin's Live TV guide. Port of pkg/epg/epg.go genXML.
/// </summary>
public class JioEpgGenerator
{
    /// <summary>Number of day-offsets to fetch (0 = today, 1 = tomorrow), matching Go's loop bound of 2.</summary>
    private const int DayOffsets = 2;

    /// <summary>HTTP fetch abstraction (mockable); post-body is the already-formatted getepg URL.</summary>
    private readonly Func<string, Task<(int Status, string Body)>> _fetcher;

    /// <summary>Source of channel list, same channel objects the playback code uses.</summary>
    private readonly Func<System.Collections.Generic.IEnumerable<Channel>> _channelsSource;

    public JioEpgGenerator(Func<string, Task<(int Status, string Body)>> fetcher, Func<System.Collections.Generic.IEnumerable<Channel>> channelsSource)
    {
        _fetcher = fetcher;
        _channelsSource = channelsSource;
    }

    /// <summary>
    /// Generates the XMLTV EPG text string from Jio's getepg API for all channels.
    /// Mirrors Go's genXML but without the progress bar visual.
    /// </summary>
    public async Task<string> GenerateAsync()
    {
        var channels = _channelsSource().Select(c => (c.Id, c.Name)).ToList();
        if (channels.Count == 0)
        {
            throw new ExternalApiException("Channel list empty; cannot generate EPG");
        }

        var programmes = new System.Collections.Generic.List<EpgProgramme>();
        var xmlChannels = new System.Collections.Generic.List<EpgChannel>();

        foreach (var (idText, name) in channels)
        {
            var id = int.Parse(idText, System.Globalization.CultureInfo.InvariantCulture);
            xmlChannels.Add(new EpgChannel { Id = id, Display = name });

            for (var offset = 0; offset < DayOffsets; offset++)
            {
                var url = EpgUrlFormatter.Format(offset, id);
                var (status, body) = await _fetcher(url).ConfigureAwait(false);
                if (status != 200 || string.IsNullOrEmpty(body))
                {
                    continue;
                }

                var payload = System.Text.Json.JsonSerializer.Deserialize<EpgResponse>(body);
                if (payload?.Epg is null || payload.Epg.Count == 0)
                {
                    continue;
                }

                foreach (var entry in payload.Epg)
                {
                    var start = EpgModel.FormatTime(
                        System.DateTimeOffset.FromUnixTimeMilliseconds(entry.StartEpoch));
                    var stop = EpgModel.FormatTime(
                        System.DateTimeOffset.FromUnixTimeMilliseconds(entry.EndEpoch));
                    programmes.Add(EpgModel.NewProgramme(
                        id, start, stop, entry.Title, entry.Description, entry.ShowCategory, entry.Poster));
                }
            }
        }

        if (programmes.Count == 0)
        {
            throw new ExternalApiException("No EPG programmes were fetched");
        }

        return EpgModel.GenXml(xmlChannels, programmes);
    }
}

/// <summary>Cached format provider for getepg URL (CA1863).</summary>
public static class EpgUrlFormatter
{
    private static readonly System.Text.CompositeFormat Composite =
        System.Text.CompositeFormat.Parse(JioConstants.EpgUrlFormat);

    /// <summary>Builds a getepg request URL.</summary>
    public static string Format(int offset, int channelId)
        => string.Format(System.Globalization.CultureInfo.InvariantCulture, Composite, offset, channelId);
}

/// <summary>Caches the EPG gzip file to disk with a per-day refresh policy.</summary>
public sealed class EpgCacheService
{
    /// <summary>Path of the cached epg.xml.gz (Jellyfin plugin dir in production).</summary>
    public string FilePath { get; }

    public EpgCacheService(string filePath)
    {
        FilePath = filePath;
    }

    /// <summary>
    /// Refresh policy port (Go pkg/epg Init): EPG only refreshes once per
    /// calendar day, on file modification date vs today's date.
    /// </summary>
    public static bool ShouldRegenerate(string fileDate, string today)
        => fileDate != today;

    /// <summary>
    /// Returns cached gzip EPG bytes if file exists and is fresh.
    /// Otherwise schedules generation via <paramref name="regenerateAsync"/>.
    /// </summary>
    public async Task<byte[]> GetOrCreateAsync(Func<Task<string>> regenerateAsync)
    {
        var exists = File.Exists(FilePath);
        var today = System.DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (!exists)
        {
            await RegenerateAsync(regenerateAsync).ConfigureAwait(false);
        }
        else
        {
            var fileDate = File.GetLastWriteTimeUtc(FilePath).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (ShouldRegenerate(fileDate, today))
            {
                await RegenerateAsync(regenerateAsync).ConfigureAwait(false);
            }
        }

        if (!File.Exists(FilePath))
        {
            throw new ExternalApiException("EPG generation failed; server continues without EPG");
        }

        return await File.ReadAllBytesAsync(FilePath).ConfigureAwait(false);
    }

    private async Task RegenerateAsync(Func<Task<string>> regenerateAsync)
    {
        var xml = await regenerateAsync().ConfigureAwait(false);
        EpgModel.GzipWrite(xml, FilePath);
    }
}

/// <summary>Mirrors the getepg JSON envelope (partial: epg items only).</summary>
public sealed class EpgResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("epg")]
    public System.Collections.Generic.List<EpgEntry>? Epg { get; set; }
}

/// <summary>Epg item (pkg/epg types.go EPGObject, trimmed).</summary>
public sealed class EpgEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("startEpoch")]
    public long StartEpoch { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("endEpoch")]
    public long EndEpoch { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("showname")]
    public string Title { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("showCategory")]
    public string ShowCategory { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("episodePoster")]
    public string Poster { get; set; } = string.Empty;
}
