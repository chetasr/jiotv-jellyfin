using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Net.Http;
using JioTv.Plugin.Auth;
using JioTv.Plugin.Network;

namespace JioTv.Plugin.Network;


/// <summary>Reads a JSON value that may be number or string into a C# string (Go-style int→string like JioTV Go's Channel.UnmarshalJSON).</summary>
public sealed class FlexibleStringConverter : JsonConverter<string>
{
    /// <inheritdoc/>
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.String => reader.GetString(),
            _ => throw new JsonException("Expected number or string"),
        };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

/// <summary>
/// Channel metadata from the JioTV channel list API.
/// JSON field names follow JioTV Go's Channel struct (types.go).
/// </summary>
public class Channel
{
    [JsonPropertyName("channel_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("channel_name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("logoUrl")]
    public string LogoUrl { get; set; } = string.Empty;

    [JsonPropertyName("channelCategoryId")]
    public int Category { get; set; }

    [JsonPropertyName("channelLanguageId")]
    public int Language { get; set; }

    [JsonPropertyName("isHD")]
    public bool IsHd { get; set; }

    [JsonPropertyName("isCatchupAvailable")]
    public bool IsCatchupAvailable { get; set; }

    /// <summary>Raw business_type from the API ("free"/"premium").</summary>
    [JsonPropertyName("business_type")]
    public string BusinessType { get; set; } = string.Empty;

    /// <summary>
    /// Derived from business_type == "premium" (see JioTV Go types.go: the
    /// API's own is_premium flag mispredicts ~1 in 5).
    /// </summary>
    [JsonPropertyName("requiresSubscription")]
    public bool RequiresSubscription { get; set; }
}

/// <summary>Bitrate variants of a stream manifest URL.</summary>
public class Bitrates
{
    [JsonPropertyName("auto")]
    public string Auto { get; set; } = string.Empty;

    [JsonPropertyName("high")]
    public string High { get; set; } = string.Empty;

    [JsonPropertyName("low")]
    public string Low { get; set; } = string.Empty;

    [JsonPropertyName("medium")]
    public string Medium { get; set; } = string.Empty;
}

/// <summary>
/// Playback geturl response. Port of JioTV Go's LiveURLOutput, trimmed to
/// fields relevant for live-only, HLS-first streaming.
/// </summary>
public class LiveResult
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;

    [JsonPropertyName("bitrates")]
    public Bitrates Bitrates { get; set; } = new();

    [JsonPropertyName("m3u8")]
    public Bitrates M3u8 { get; set; } = new();

    [JsonPropertyName("isDRM")]
    public bool IsDrm { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Parsed __hdnea__ token found in one of the response URLs.</summary>
    [JsonIgnore]
    public string Hdnea => JioTvClient.ExtractHdneaFromUrl(Result) is { } a && !string.IsNullOrEmpty(a)
        ? a
        : new[] { Bitrates.Auto, Bitrates.High, Bitrates.Medium, Bitrates.Low }
            .Select(JioTvClient.ExtractHdneaFromUrl)
            .FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? string.Empty;
}

/// <summary>Wrapper for the channel list API envelope.</summary>
public sealed class ChannelsPayload
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("result")]
    public List<Channel> Result { get; set; } = new();
}

/// <summary>
/// Calls the JioTV playback + channel listing APIs. Port of
/// JioTV Go's Television struct (television.go New/Live/Channels).
/// </summary>
public class JioTvClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly JioCredentials _creds;
    private readonly Func<HttpClient> _clientFactory;

    /// <summary>Maximum number of channels we accept before flagging suspicious responses.</summary>
    private const int MaxPlausibleChannels = 2000;

    /// <summary>Creates a client tied to the given credentials snapshot.</summary>
    public JioTvClient(JioCredentials creds, Func<HttpClient>? clientFactory = null)
    {
        _creds = creds;
        _clientFactory = clientFactory ?? (() => JioHttp.HttpClient);
    }

    /// <summary>
    /// Requests a playable manifest URL for the channel. Port of Television.Live.
    /// Throws ExternalApiException when the API returns a non-200 code.
    /// </summary>
    public async Task<LiveResult> LiveAsync(string channelId)
    {
        // Go: channel_id, stream_type=Seek, begin=now, srno=date
        var begin = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var srno = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var form = $"channel_id={Uri.EscapeDataString(channelId)}&stream_type=Seek&begin={begin}&srno={srno}";

        using var request = JioHttp.BuildFormPost(
            $"https://{JioConstants.JioTvApiDomain}{JioConstants.PlaybackApiPath}",
            form,
            accessToken: _creds.AccessToken);

        request.Headers.TryAddWithoutValidation("appkey", "NzNiMDhlYzQyNjJm");
        request.Headers.TryAddWithoutValidation("channel_id", channelId);
        request.Headers.TryAddWithoutValidation("crmid", _creds.CRM);
        request.Headers.TryAddWithoutValidation("userId", _creds.CRM);
        request.Headers.TryAddWithoutValidation("deviceId", _creds.DeviceId);
        request.Headers.TryAddWithoutValidation("devicetype", JioConstants.DeviceTypePhone);
        request.Headers.TryAddWithoutValidation("isott", "false");
        request.Headers.TryAddWithoutValidation("languageId", "6");
        request.Headers.TryAddWithoutValidation("lbcookie", "1");
        request.Headers.TryAddWithoutValidation("os", JioConstants.OsAndroid);
        request.Headers.TryAddWithoutValidation("osVersion", "13");
        request.Headers.TryAddWithoutValidation("subscriberId", _creds.CRM);
        request.Headers.TryAddWithoutValidation("uniqueId", _creds.UniqueId);
        request.Headers.TryAddWithoutValidation("usergroup", JioConstants.UserGroup);
        request.Headers.TryAddWithoutValidation("versionCode", JioConstants.VersionCode);
        request.Headers.TryAddWithoutValidation("User-Agent", JioConstants.UserAgentOkHttp);

        var response = await _clientFactory().SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        var result = JsonSerializer.Deserialize<LiveResult>(body, JsonOpts);
        if (result is null)
        {
            throw new ExternalApiException($"Playback response empty for channel {channelId}");
        }

        if (result.Code != 200)
        {
            throw new ExternalApiException($"Playback API returned code {result.Code} for channel {channelId}: {result.Message}");
        }

        return result;
    }

    /// <summary>
    /// Fetches channel list from the default (unauthenticated) endpoint.
    /// Port of television.Channels fallback path; auth-aware listing is
    /// deferred to a later task once tested against a real account.
    /// </summary>
    public async Task<List<Channel>> GetChannelsAsync()
    {
        using var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, JioConstants.ChannelsApiUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", JioConstants.UserAgentOkHttp);
        request.Headers.TryAddWithoutValidation("Accept", JioConstants.AcceptJson);
        request.Headers.TryAddWithoutValidation("devicetype", JioConstants.DeviceTypePhone);
        request.Headers.TryAddWithoutValidation("os", JioConstants.OsAndroid);
        request.Headers.TryAddWithoutValidation("appkey", "NzNiMDhlYzQyNjJm");
        request.Headers.TryAddWithoutValidation("lbcookie", "1");
        request.Headers.TryAddWithoutValidation("usertype", "JIO");

        var response = await _clientFactory().SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        var channelPayload = JsonSerializer.Deserialize<ChannelsPayload>(body, JsonOpts);
        if (channelPayload?.Result is null || channelPayload.Result.Count > MaxPlausibleChannels)
        {
            throw new ExternalApiException("Channel list response malformed or implausibly large");
        }

        foreach (var channel in channelPayload.Result)
        {
            channel.RequiresSubscription = channel.BusinessType == "premium";
        }

        return channelPayload.Result;
    }

    /// <summary>
    /// Picks manifest URL for a quality; mirrors SelectQuality semantics:
    /// exact match first, then auto/high/medium/low fallback.
    /// </summary>
    public static string SelectQuality(string quality, string auto, string high, string medium, string low)
    {
        var mapping = new Dictionary<string, string>
        {
            { "auto", auto }, { "high", high }, { "medium", medium }, { "low", low },
        };
        if (mapping.TryGetValue(quality, out var selected) && !string.IsNullOrEmpty(selected))
        {
            return selected;
        }

        return new[] { auto, high, medium, low }.FirstOrDefault(u => !string.IsNullOrEmpty(u)) ?? string.Empty;
    }

    /// <summary>Extracts the __hdnea__ query token from a URL if present.</summary>
    public static string ExtractHdneaFromUrl(string url)
        => TryExtractHdneaFromUrl(url, out var token) ? token : string.Empty;

    /// <summary>Extracts the __hdnea__ query token; false when absent.</summary>
    public static bool TryExtractHdneaFromUrl(string url, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var value = query.Get("__hdnea__");
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        token = value;
        return true;
    }

    private static string ExtractHdneaFromCandidates(params string[] candidates)
        => candidates.Select(ExtractHdneaFromUrl).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? string.Empty;
}
