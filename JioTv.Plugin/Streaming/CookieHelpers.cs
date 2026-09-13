using System;

#pragma warning disable CS1591

namespace JioTv.Plugin.Streaming;

/// <summary>Helpers for parsing rotated Akamai cookies from Set-Cookie headers.</summary>
public static class CookieHelpers
{
    /// <summary>
    /// Extracts a usable __hdnea__ token from a Set-Cookie value, mirroring
    /// Go's handling: requires exp= to be present, URL-decodes the payload.
    /// </summary>
    public static string ParseRotatedHdnea(string setCookie)
    {
        if (string.IsNullOrEmpty(setCookie) ||
            !setCookie.Contains("__hdnea__=", System.StringComparison.Ordinal) ||
            !setCookie.Contains("exp=", System.StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var start = setCookie.IndexOf("__hdnea__=", System.StringComparison.Ordinal) + "__hdnea__=".Length;
        if (endIndex(setCookie, start) < start)
        {
            return string.Empty;
        }

        var value = setCookie[start..endIndex(setCookie, start)];
        return WebUrlDecodeBestEffort(value);
    }

    private static int endIndex(string value, int start)
    {
        var semi = value.IndexOf(';', start);
        return semi < 0 ? value.Length : semi;
    }

    private static string WebUrlDecodeBestEffort(string urlEncoded)
    {
        try
        {
            return System.Uri.UnescapeDataString(urlEncoded);
        }
        catch (UriFormatException)
        {
            return string.Empty;
        }
    }
}
