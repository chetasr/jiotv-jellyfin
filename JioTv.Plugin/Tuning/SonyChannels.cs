using System;
using System.Collections.Generic;
using System.Net.Http;
using JioTv.Plugin.Auth;
using Core = JioTv.Plugin.Network;

#pragma warning disable CS1591

namespace JioTv.Plugin.Tuning;

/// <summary>
/// SonyLIV channel catalog (port of JioTV Go's SONY_CHANNELS_API/SONY_CHANNELS).
/// Ids use the "sl" prefix followed by the Jio channel number; each maps to a
/// Google DAI live-event URL that redirects onto SonyLIV's CDN
/// (lin-gd-001-cf.slivcdn.com). No Sony credentials are required: the DAI
/// redirect is the "sony auth path".
/// </summary>
public static class SonyChannels
{
    /// <summary>Base64-encoded Google DAI master.m3u8 urls (JioTV Go parity).</summary>
    private static readonly Dictionary<string, string> Map = new()
    {
        ["sl291"] = "aHR0cHM6Ly9kYWkuZ29vZ2xlLmNvbS9saW5lYXIvaGxzL2V2ZW50L2RCZHdPaUdhUXZ5MFRBMXpPc2pWNncvbWFzdGVyLm0zdTg=",
        ["sl154"] = "aHR0cHM6Ly9kYWkuZ29vZ2xlLmNvbS9saW5lYXIvaGxzL2V2ZW50L0NyVGl2a0RFU1dxd3ZVajN6RkVZRUEvbWFzdGVyLm0zdTg=",
        ["sl474"] = "aHR0cHM6Ly9kYWkuZ29vZ2xlLmNvbS9saW5lYXIvaGxzL2V2ZW50L2RoUHJHUndEUnZ1TVF0bWx6cHB6UVEvbWFzdGVyLm0zdTg=",
        ["sl762"] = "aHR0cHM6Ly9kYWkuZ29vZ2xlLmNvbS9saW5lYXIvaGxzL2V2ZW50L3g3clhXZDJFUloydHZ5UVdQbU8xSEEvbWFzdGVyLm0zdTg=",
        ["sl476"] = "aHR0cHM6Ly9kYWkuZ29vZ2xlLmNvbS9saW5lYXIvaGxzL2V2ZW50L1VjakhOSm1DUTFXUmxHS2xabTczUUEvbWFzdGVyLm0zdTg=",
        ["sl483"] = "aHR0cHM6Ly9kYWkuZ29vZ2xlLmNvbS9saW5lYXIvaGxzL2V2ZW50L01kUTVaeS1QU3JhT2NjWHU4amZsQ2cvbWFzdGVyLm0zdTg=",
        ["sl1393"] = "aHR0cHM6Ly9kYWkuZ29vZ2xlLmNvbS9saW5lYXIvaGxzL2V2ZW50L2dYNXJDQmY2UTctRDVBV1lTb3Z6UXEvbWFzdGVyLm0zdTg=",
        ["sl162"] = "aHR0cHM6Ly9kYWkuZ29vZ2xlLmNvbS9saW5lYXIvaGxzL2V2ZW50L3dHNzVuNVU4UnJPS2lGemFXT2JYYkEvbWFzdGVyLm0zdTg=",
        ["sl891"] = "aHR0cHM6Ly9kYWkuZ29vZ2xlLmNvbS9saW5lYXIvaGxzL2V2ZW50L1Y5aC1peU94UmlHcDQxcHBRU2NEU1EvbWFzdGVyLm0zdTg=",
        ["sl892"] = "aHR0cHM6Ly9kYWkuZ29vZ2xlLmNvbS9saW5lYXIvaGxzL2V2ZW50L2x0c0NHN1RCU0NTRG15cTByUXR2U0EvbWFzdGVyLm0zdTg=",
        ["sl1772"] = "aHR0cHM6Ly9kYWkuZ29vZ2xlLmNvbS9saW5lYXIvaGxzL2V2ZW50L3NtWXliSV9KVG9XYUh6d294U0U5cUEvbWFzdGVyLm0zdTg=",
        ["sl155"] = "aHR0cHM6Ly9kYWkuZ29vZ2xlLmNvbS9saW5lYXIvaGxzL2V2ZW50L1NsZV9UUjhyUUl1WkhXenNoRVhZalEvbWFzdGVyLm0zdTg=",
        ["sl852"] = "aHR0cHM6Ly9kYWkuZ29vZ2xlLmNvbS9saW5lYXIvaGxzL2V2ZW50LzZiVldZSUtHUzBDSWEtY09wWlpKUFEvbWFzdGVyLm0zdTg=",
    };

    private static readonly Dictionary<string, SonyCatalogEntry> Catalog = new()
    {
        ["sl291"] = new SonyCatalogEntry("SL Sony HD", "Sony_HD.png", 1, 5, true),
        ["sl154"] = new SonyCatalogEntry("SL Sony SAB HD", "Sony_SAB_HD.png", 1, 5, true),
        ["sl474"] = new SonyCatalogEntry("SL Sony PAL", "Sony_Pal.png", 1, 5, false),
        ["sl762"] = new SonyCatalogEntry("SL Sony PIX HD", "Sony_Pix_HD.png", 6, 6, true),
        ["sl476"] = new SonyCatalogEntry("SL Sony MAX HD", "Sony_Max_HD.png", 1, 6, true),
        ["sl483"] = new SonyCatalogEntry("SL Sony MAX 2", "Sony_MAX2.png", 1, 6, false),
        ["sl1393"] = new SonyCatalogEntry("SL Sony WAH", "Sony_Wah.png", 1, 5, false),
        ["sl162"] = new SonyCatalogEntry("SL Sony TEN 1 HD", "Ten_HD.png", 6, 8, true),
        ["sl891"] = new SonyCatalogEntry("SL Sony TEN 2 HD", "Ten2_HD.png", 6, 8, true),
        ["sl892"] = new SonyCatalogEntry("SL Sony TEN 3 HD", "Ten3_HD.png", 1, 8, true),
        ["sl1772"] = new SonyCatalogEntry("SL Sony TEN 4 HD", "Ten_4_HD_Tamil.png", 8, 8, true),
        ["sl155"] = new SonyCatalogEntry("SL Sony TEN 5 HD", "Six_HD.png", 6, 8, true),
        ["sl852"] = new SonyCatalogEntry("SL Sony BBC Earth HD", "Sony_BBC_Earth_HD_English.png", 6, 10, true),
    };

    /// <summary>Whether this tuner id is a SonyLIV channel ("sl" prefix).</summary>
    public static bool IsSony(string id) => !string.IsNullOrEmpty(id)
        && id.StartsWith("sl", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves the Google DAI master m3u8 URL for the sony channel id.</summary>
    public static string GetDaiUrl(string sonyId)
    {
        if (!Map.TryGetValue(sonyId, out var encoded))
        {
            throw new ExternalApiException($"Unknown SonyLIV channel id {sonyId}");
        }

        return System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(encoded));
    }

    /// <summary>Builds JioTV-channel shaped entries for every SonyLIV channel.</summary>
    public static IEnumerable<Core.Channel> ToJioChannelList()
    {
        foreach (var (id, meta) in Catalog)
        {
            yield return new Core.Channel
            {
                Id = id,
                Name = meta.Name,
                LogoUrl = meta.Logo,
                Language = meta.Language,
                Category = meta.Category,
                IsHd = meta.IsHd,
            };
        }
    }

    /// <summary>Fetches the SonyLIV playable stream URL by following the DAI redirect.</summary>
    public static async System.Threading.Tasks.Task<string> ResolveSonyLiveUrlAsync(
        string sonyId, System.Threading.CancellationToken ct)
    {
        var daiUrl = GetDaiUrl(sonyId);
        using var request = new HttpRequestMessage(HttpMethod.Get, daiUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/3.14.9");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.jiocinema.com/");
        using var response = await Network.JioHttp.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if ((int)response.StatusCode != 302 && (int)response.StatusCode != 200)
        {
            throw new ExternalApiException($"SonyLIV DAI unreachable for {sonyId} (status {(int)response.StatusCode})");
        }

        var location = response.Headers.Location?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(location))
        {
            // The DAI may answer 200 directly with a manifest body — hand it back verbatim.
            return daiUrl;
        }

        return location;
    }
}

/// <summary>SonyLIV channel catalog metadata entry.</summary>
public sealed record SonyCatalogEntry(string Name, string Logo, int Language, int Category, bool IsHd);
