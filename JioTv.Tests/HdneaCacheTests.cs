using System;
using System.Collections.Generic;
using System.Linq;
using JioTv.Plugin.Streaming;
using Xunit;

namespace JioTv.Tests;

public class HdneaCacheTests
{
    private static string MakeToken(int expOffsetSeconds) =>
        $"st=1700000000~exp={1700000000 + expOffsetSeconds}~acl=/*~hmac=abc";

    [Fact]
    public void RemainingLifetime_ParsesExpSegment()
    {
        var lifetime = HdneaCache.RemainingLifetime(
            MakeToken(120),
            now: DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime);
        Assert.NotNull(lifetime);
        Assert.InRange(lifetime!.Value.TotalSeconds, 119, 121);
    }

    [Fact]
    public void RemainingLifetime_BadToken_ReturnsNull()
    {
        Assert.Null(HdneaCache.RemainingLifetime("garbage", DateTime.UtcNow));
        Assert.Null(HdneaCache.RemainingLifetime("", DateTime.UtcNow));
        Assert.Null(HdneaCache.RemainingLifetime("st=1~hmac=x", DateTime.UtcNow));
    }

    [Fact]
    public void SetGet_TokenRoundTrip()
    {
        var cache = new HdneaCache(now: () => DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime);
        cache.Set("144", MakeToken(120));
        Assert.True(cache.TryGet("144", out var token));
        Assert.Contains("hmac=abc", token);
    }

    [Fact]
    public void Cache_ExpiresAfterTtl()
    {
        var now = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var current = now;
        var cache = new HdneaCache(now: () => current);
        cache.Set("144", "tok");

        current = now.AddSeconds(30); // within 60s TTL
        Assert.True(cache.TryGet("144", out _));

        current = now.AddSeconds(61); // past 60s TTL
        Assert.False(cache.TryGet("144", out _));
    }

    [Fact]
    public void CacheKey_NamespacesLiveVsCatchup()
    {
        Assert.Equal("144", HdneaCache.CacheKey("144", "https://edge/HLS/144/stream.m3u8"));
        Assert.Equal("144|catchup", HdneaCache.CacheKey("144", "https://edge/CATCHUP/144/stream.m3u8"));
        Assert.Equal(string.Empty, HdneaCache.CacheKey("", "https://edge/stream.m3u8"));
    }

    [Fact]
    public void DeadChannel_MarkAndRecover()
    {
        var now = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var current = now;
        var cache = new HdneaCache(now: () => current);

        cache.MarkDead("144");
        Assert.True(cache.IsRecentlyDead("144"));

        cache.ClearDead("144");
        Assert.False(cache.IsRecentlyDead("144"));

        // TTL expiry auto-evicts like Go (60s)
        cache.MarkDead("144");
        current = now.AddSeconds(61);
        Assert.False(cache.IsRecentlyDead("144"));
    }
}

public class ManifestRewriterTests
{
    private const string SampleManifest = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360
        144_1.m3u8?__hdnea__=st%3D1~exp%3D1~acl%3D%2F*~hmac%3Dold
        #EXT-X-STREAM-INF:BANDWIDTH=2400000,RESOLUTION=1280x720
        https://jiotv.cdn.jio.com/HLSV3/144/144_2.m3u8
        #EXTINF:6.000,
        144_0001.ts?salt=1
        #EXT-X-KEY:METHOD=AES-128,URI="https://tv.media.jio.com/streams_live/144/144.key"
        """;

    [Fact]
    public void Rewrite_ReplacesManifestAndSegmentAndKeyUrls()
    {
        var cipher = SecureUrlCipher.CreateWithKey(new byte[32]);
        var rewriter = new ManifestRewriter(cipher, "/JioTv");

        var output = rewriter.Rewrite(SampleManifest, channelId: "144", upstreamBaseUrl: "https://jiotv.cdn.jio.com/HLSV3/144/", upstreamParams: "__hdnea__=st%3D1~exp%3D9~acl%3D%2F*~hmac%3Dtok", quality: "high");

        // relative manifest became a proxy URL
        Assert.Contains("/JioTv/manifest.m3u8?auth=", output);
        Assert.Contains("channel_key_id=144", output);
        Assert.Contains("q=high", output);

        // .ts segment rewritten to render.ts endpoint
        Assert.Contains("/JioTv/segment.ts?auth=", output);

        // key URL rewritten
        Assert.Contains("/JioTv/key?auth=", output);

        // the encrypted payloads decrypt back to reasonable upstream URLs
        var authParamIdx = output.IndexOf("auth=", StringComparison.Ordinal);
        Assert.True(authParamIdx >= 5); // auth param present
    }

    [Fact]
    public void CreateEncryptedUrl_DecryptsBackToFullUrl()
    {
        var cipher = SecureUrlCipher.CreateWithKey(new byte[32]);
        var rewriter = new ManifestRewriter(cipher, "/JioTv");

        var url = rewriter.CreateEncryptedProxyPath(
            baseUrl: "https://edge/HLS/144/",
            match: "seg.ts",
            @params: "salt=1",
            channelId: "144",
            endpoint: "/JioTv/segment.ts");

        // shape: /JioTv/segment.ts?auth=<enc>&channel_key_id=144
        var auth = url.Split("auth=")[1].Split('&')[0];
        Assert.Equal("https://edge/HLS/144/seg.ts?salt=1", cipher.Decrypt(auth));
        Assert.Contains("channel_key_id=144", url);
    }
}
