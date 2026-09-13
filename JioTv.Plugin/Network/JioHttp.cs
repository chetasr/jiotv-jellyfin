using System;
using System.Net.Http;
using System.Text;

namespace JioTv.Plugin.Network;

/// <summary>
/// Shared HTTP client and helpers for all JioTV API traffic.
/// Single point of control for timeouts, retry glue and request building.
/// </summary>
public static class JioHttp
{
    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            UseCookies = false,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online,
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    });

    /// <summary>
    /// Gets the shared HttpClient for all JioTV traffic. JioTV Go uses
    /// fasthttp with a 10s timeout; we mirror that.
    /// </summary>
    public static HttpClient HttpClient => Client.Value;

    /// <summary>
    /// Builds a POST request carrying application/x-www-form-urlencoded data
    /// as used by the playback geturl API.
    /// </summary>
    /// <param name="url">Target URL.</param>
    /// <param name="form">key=value pairs (already URL-encoded).</param>
    /// <param name="accessToken">Access token for the accessToken header; empty to omit.</param>
    /// <returns>The request ready to send via <see cref="HttpClient"/>.</returns>
    public static HttpRequestMessage BuildFormPost(string url, string form, string accessToken = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(form, Encoding.UTF8, JioConstants.ContentTypeFormUrlEncoded);
        if (!string.IsNullOrEmpty(accessToken))
        {
            request.Headers.TryAddWithoutValidation("accessToken", accessToken);
        }

        return request;
    }

    /// <summary>
    /// Builds a JSON POST request (OTP send/verify, token refresh).
    /// </summary>
    /// <returns>The request ready to send via <see cref="HttpClient"/>.</returns>
    /// <param name="url">Target URL.</param>
    /// <param name="jsonBody">JSON request body.</param>
    public static HttpRequestMessage BuildJsonPost(string url, string jsonBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, JioConstants.ContentTypeJson);
        return request;
    }
}
