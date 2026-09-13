using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using JioTv.Plugin.Auth;
using JioTv.Plugin.Network;
using Xunit;

namespace JioTv.Tests;

/// <summary>
/// JioTvClient behavior against mocked Jio API responses.
/// </summary>
public class JioTvClientTests
{
    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task LiveAsync_SendsExpectedHeadersAndForm()
    {
        var handler = new PlaybackMockHandler
        {
            Respond = _ => Json(HttpStatusCode.OK,
                "{\"code\":200,\"result\":\"https://jiotv.cdn/full.m3u8?__hdnea__=exp%3D1~hmac%3Dabc\",\"bitrates\":{\"auto\":\"\"}}"),
        };
        var client = JioTvTestFactory.Create(handler, access: "at", crm: "crm1", unique: "u1", deviceId: "dev1");

        var result = await client.LiveAsync("144");

        var request = handler.SentRequests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/playback/apis/v1.1/geturl?langId=6", request.RequestUri!.ToString());
        Assert.Equal("at", request.Headers.GetValues("accessToken").First());
        Assert.Equal("crm1", request.Headers.GetValues("crmid").First());

        var form = handler.LastFormBody;
        Assert.NotNull(form);
        Assert.Contains("channel_id=144", form);
        Assert.Contains("stream_type=Seek", form);
        Assert.True(result!.Code == 200);
        Assert.Equal("https://jiotv.cdn/full.m3u8?__hdnea__=exp%3D1~hmac%3Dabc", result.Result);
    }

    [Fact]
    public void ExtractHdnea_FindsTokenInResultUrls()
    {
        var url = "https://edge.fullstream.tv/HLSV3/144/index.m3u8?__hdnea__=st=1700000000~exp=1700080000~acl=/*~hmac=99";
        var token = JioTvClient.ExtractHdneaFromUrl(url);
        Assert.Contains("hmac=", token);
        Assert.StartsWith("st=", token);
    }

    [Fact]
    public void ExtractHdnea_MissingTokenReturnsEmpty()
    {
        Assert.False(JioTvClient.TryExtractHdneaFromUrl("https://edge/stream.m3u8", out var token));
        Assert.Equal(string.Empty, token);
    }

    [Fact]
    public void SelectQuality_ProvidersFallbackOrder()
    {
        Assert.Equal("hi.m3u8", JioTvClient.SelectQuality("high",
            auto: "auto.m3u8", high: "hi.m3u8", medium: "med.m3u8", low: "lo.m3u8"));
        Assert.Equal("auto.m3u8", JioTvClient.SelectQuality("sparkle",
            auto: "auto.m3u8", high: "hi.m3u8", medium: "med.m3u8", low: "lo.m3u8"));
    }

    [Fact]
    public void LiveAsync_Non200_Throws()
    {
        var handler = new PlaybackMockHandler
        {
            Respond = _ => Json(HttpStatusCode.OK, "{\"code\":401,\"message\":\"token expired\"}"),
        };
        var client = JioTvTestFactory.Create(handler);
        var ex = Assert.Throws<ExternalApiException>(() => client.LiveAsync("144").GetAwaiter().GetResult());
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task GetChannels_ParsesChannelList()
    {
        var handler = new PlaybackMockHandler
        {
            Respond = _ => Json(HttpStatusCode.OK, """
            {"code":200,"result":[
              {"channel_id":144,"channel_name":"NDTV","logoUrl":"https://logo/144.png","channelCategoryId":1,"channelLanguageId":1,"isHD":true,"isCatchupAvailable":false,"business_type":"free"},
              {"channel_id":"467","channel_name":"Sports1","business_type":"premium"}
            ]}
            """),
        };
        var client = JioTvTestFactory.Create(handler);

        var channels = await client.GetChannelsAsync();

        Assert.Equal(2, channels!.Count);
        Assert.Equal("144", channels[0].Id);
        Assert.Equal("NDTV", channels[0].Name);
        Assert.False(channels[0].RequiresSubscription);
        Assert.True(channels[1].RequiresSubscription); // business_type premium
    }
}
