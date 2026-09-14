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
            !setCookie.Contains("__hdnea__=", StringComparison.Ordinal) ||
            !setCookie.Contains("exp=", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var start = setCookie.IndexOf("__hdnea__=", StringComparison.Ordinal) + "__hdnea__=".Length;
        var semi = setCookie.IndexOf(';', start);
        var end = semi < 0 ? setCookie.Length : semi;
        var value = setCookie[start..end];
        return UnescapeBestEffort(value);
    }

    private static string UnescapeBestEffort(string urlEncoded)
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

    /// <summary>
    /// Port of Go's RenderKeyHandler cookie conversion: every query param of
    /// the decoded key URL becomes a Cookie entry (minrate/maxrate/__hdnea__…),
    /// with values URL-decoded (the token arrives percent-encoded inside the
    /// query, so the Authamai form matches exp=… on the cookie wall).
    /// </summary>
    public static string BuildKeyCookies(string decodedUrl)
    {
        var cut = decodedUrl.IndexOf('?', StringComparison.Ordinal);
        if (cut < 0 || cut + 1 >= decodedUrl.Length)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var pair in decodedUrl[(cut + 1)..].Split('&'))
        {
            var nameEnd = pair.IndexOf('=', StringComparison.Ordinal);
            var name = nameEnd < 0 ? pair : pair[..nameEnd];
            var value = nameEnd < 0 ? string.Empty : pair[(nameEnd + 1)..];
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(name).Append('=').Append(UnescapeBestEffort(value));
        }

        return builder.ToString();
    }
}
