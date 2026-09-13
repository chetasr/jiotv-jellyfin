using System.Net.Http;
using JioTv.Plugin.Auth;
using JioTv.Plugin.Network;

namespace JioTv.Tests;

/// <summary>
/// Builds a JioTvClient wired to a mock HttpMessageHandler for tests.
/// </summary>
public static class JioTvTestFactory
{
    public static JioTvClient Create(
        HttpMessageHandler handler,
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
        return new JioTvClient(creds, () => new HttpClient(handler) { Timeout = System.TimeSpan.FromSeconds(10) });
    }
}
