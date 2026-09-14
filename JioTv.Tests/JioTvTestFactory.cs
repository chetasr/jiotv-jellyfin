using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using JioTv.Plugin.Auth;
using JioTv.Plugin.Network;

namespace JioTv.Tests;

/// <summary>
/// Mock HTTP handler reused across tests: returns canned responses and
/// records the last request so forms/headers can be asserted.
/// </summary>
public class PlaybackMockHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }
    public string? LastFormBody { get; private set; }
    public List<HttpRequestMessage> SentRequests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
    {
        SentRequests.Add(request);
        LastFormBody = request.Content is null
            ? null
            : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        return Task.FromResult(Respond!(request));
    }
}

/// <summary>
/// Builds a JioTvClient wired to a mock HttpMessageHandler for tests.
/// </summary>
public static class JioTvTestFactory
{
    public static JioTvClient Create(
        PlaybackMockHandler? handler = null,
        string access = "accesstoken",
        string sso = "ssotoken",
        string crm = "1234567890",
        string unique = "uniqueid",
        string deviceId = "testdevice")
    {
        var creds = new JioCredentials
        {
            AccessToken = access,
            SSOToken = sso,
            CRM = crm,
            UniqueId = unique,
            DeviceId = deviceId,
        };
        var response = "{ \"code\": 200, \"result\": \"https://cdn.auto.m3u8\", \"bitrates\": { \"auto\": \"https://cdn.auto.m3u8\" } }";
        handler ??= new PlaybackMockHandler
        {
            Respond = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            },
        };
        return new JioTvClient(creds, () => new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) });
    }

    /// <summary>Client whose LiveAsync always returns the standard auto m3u8 URL (for stream-source tests).</summary>
    public static JioTvClient CreatePlaybackClient()
    {
        return Create(
            new PlaybackMockHandler
            {
                Respond = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{ \"code\": 200, \"result\": \"https://cdn.auto.m3u8\", \"bitrates\": { \"auto\": \"https://cdn.auto.m3u8\", \"high\": \"https://cdn.high.m3u8\", \"medium\": \"\", \"low\": \"\" } }",
                        Encoding.UTF8, "application/json"),
                },
            });
    }
}

