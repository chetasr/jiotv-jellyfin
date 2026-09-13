using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JioTv.Plugin.Network;
using JioTv.Plugin.Streaming;
using Xunit;

namespace JioTv.Tests;

/// <summary>
/// Ports of the self-heal decision tree from JioTV Go's RenderHandler:
/// 200 passthrough, 401/403 token-refresh-retry, 404 quality ladder, and the
/// dead-channel recovery throttle.
/// </summary>
public class HlsProxyRendererTests
{
    private static readonly byte[] TestKey = new byte[32];

    private static JioTv.Plugin.Streaming.ManifestRewriter MakeRewriter() =>
        new(SecureUrlCipher.CreateWithKey(TestKey), "/JioTv");

    private sealed class FakeUpstream : JioTv.Plugin.Streaming.IUpstream
    {
        public Func<(string url, string hdnea), (int Status, string Body, string NewHdnea)>? Step { get; set; }
        public LiveResult? LiveOutput { get; set; }
        public List<(string Url, string Hdnea)> Calls { get; } = new();
        public int LiveCalls { get; private set; }

        public Task<(int Status, string Body, string NewHdnea)> RenderAsync(string url, string hdnea)
        {
            Calls.Add((url, hdnea));
            return Task.FromResult(Step!.Invoke((url, hdnea)));
        }

        public Task<LiveResult> LiveAsync(string channelId)
        {
            LiveCalls++;
            return Task.FromResult(LiveOutput ?? new LiveResult { Code = 200, Result = "https://cdn/fresh.m3u8?__hdnea__=exp%3D1~hmac%3Dnew" });
        }
    }

    private readonly HdneaCache _cache = new(now: () => DateTime.UtcNow);

    [Fact]
    public async Task Render_200_RewrittenAndNewTokenCached()
    {
        var upstream = new FakeUpstream
        {
            Step = _ => (200, "OK #EXTM3U seg.ts", "__hdnea__=exp%3D1~hmac%3Dfresh"),
        };
        var renderer = new HlsProxyRenderer(upstream, _cache, MakeRewriter());

        var result = await renderer.RenderManifestAsync("144", "https://cdn/index.m3u8?__hdnea__=exp%3D1~hmac%3Dold", qa: "high");

        Assert.Equal(200, result.StatusCode);
        Assert.Contains("fresh", result.NewHdneaExactly);
        Assert.True(_cache.TryGet("144", out var cached));
        Assert.Contains("fresh", cached);
    }

    [Fact]
    public async Task Render_403_RerunsLiveAndRetriesWithFreshToken()
    {
        int call = 0;
        var upstream = new FakeUpstream
        {
            LiveOutput = new LiveResult { Code = 200, Result = "https://cdn/fresh.m3u8?__hdnea__=exp%3D1~hmac%3Drefreshed" },
            Step = _ =>
            {
                call++;
                return call == 1
                    ? (403, string.Empty, string.Empty)
                    : (200, "fresh playlist", string.Empty);
            },
        };
        var renderer = new HlsProxyRenderer(upstream, _cache, MakeRewriter());

        var result = await renderer.RenderManifestAsync("144", "https://cdn/index.m3u8?__hdnea__=exp%3D1~hmac%3Dstale", "auto");

        Assert.Equal(200, result.StatusCode);
        Assert.True(upstream.Calls.Count >= 2, "expected retry after refresh harvest");
    }

    [Fact]
    public async Task Render_404_MarksChannelDeadAndTriesQualityLadder()
    {
        int call = 0;
        var upstream = new FakeUpstream
        {
            LiveOutput = new LiveResult
            {
                Code = 200,
                Result = "https://cdn/high.m3u8",
                Bitrates = new Bitrates { Auto = "https://cdn/auto.m3u8", High = "https://cdn/high.m3u8", Medium = "https://cdn/med.m3u8", Low = "https://cdn/lo.m3u8" },
            },
        };
        upstream.Step = _ => { call++; return (404, string.Empty, string.Empty); };
        var renderer = new HlsProxyRenderer(upstream, _cache, MakeRewriter());

        var result = await renderer.RenderManifestAsync("144", "https://cdn/index.m3u8", "high");

        Assert.Equal(404, result.StatusCode);
        Assert.True(_cache.IsRecentlyDead("144"), "channel should be marked dead after full ladder");
        Assert.True(upstream.LiveCalls == 1);
    }

    [Fact]
    public async Task Render_DeathThrottle_SkipsRecoveryForRecentlyDeadChannel()
    {
        _cache.MarkDead("144");
        var upstream = new FakeUpstream
        {
            Step = _ => (404, string.Empty, string.Empty),
        };
        var renderer = new HlsProxyRenderer(upstream, _cache, MakeRewriter());

        var result = await renderer.RenderManifestAsync("144", "https://cdn/index.m3u8", "auto");

        Assert.Equal(404, result.StatusCode);
        Assert.Equal(0, upstream.LiveCalls); // no expensive recovery runs
    }

    [Fact]
    public async Task Render_SuccessfulRecovery_ClearsDeath()
    {
        _cache.MarkDead("144");
        var upstream = new FakeUpstream
        {
            LiveOutput = new LiveResult { Code = 200, Result = "https://cdn/fresh.m3u8", Bitrates = new Bitrates { Auto = "https://cdn/fresh.m3u8" } },
            Step = _ => (200, "recovered", string.Empty),
        };
        var renderer = new HlsProxyRenderer(upstream, _cache, MakeRewriter());

        await renderer.RenderManifestAsync("144", "https://cdn/index.m3u8", "auto");

        Assert.False(_cache.IsRecentlyDead("144"));
    }
}
