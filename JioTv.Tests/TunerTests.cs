using System;
using System.Collections.Generic;
using System.Linq;
using JioTv.Plugin.Auth;
using JioTv.Plugin.Network;
using JioTv.Plugin.Streaming;
using JioTv.Plugin.Tuning;
using MediaBrowser.Model.Dto;
using Xunit;

namespace JioTv.Tests;

/// <summary>
/// Unit coverage of the Task 8 tuner plumbing (mapper + stream source), with
/// the contract verification of ITunerHost/ILiveStream shapes done against
/// the real Jellyfin MediaBrowser.Controller source at /tmp/pi-github-repos.
/// </summary>
public class TunerTests
{
    private readonly List<Channel> _channels = new()
    {
        new Channel { Id = "144", Name = "NDTV 24x7", LogoUrl = "https://logo/144.png" },
        new Channel { Id = "467", Name = "Sports 1" },
        new Channel { Id = "896", Name = "Devotional", LogoUrl = string.Empty },
    };

    [Fact]
    public void Map_AssignsSequentialNumbersAndIds()
    {
        var mapped = JioTvChannelMapper.Map(_channels, "jiotv-tuner-1").ToList();

        Assert.Equal("1", mapped[0].Number);
        Assert.Equal("2", mapped[1].Number);
        Assert.Equal("3", mapped[2].Number);
        Assert.Equal("144", mapped[0].Id);
        Assert.Contains("144", mapped[0].Path);
    }

    [Fact]
    public void Map_CarriesLogoWhenPresent()
    {
        var mapped = JioTvChannelMapper.Map(_channels, "tuner").ToList();
        Assert.Equal("https://logo/144.png", mapped[0].ImagePath);
        Assert.True(mapped[1].ImagePath is null);
    }

    [Fact]
    public void StreamSource_BuildsOpaqueProxyUrl()
    {
        var cipher = SecureUrlCipher.CreateWithKey(new byte[32]);
        var rewriter = new ManifestRewriter(cipher, "/JioTv");
        var source = new JioTvStreamSource(JioTvTestFactory.CreatePlaybackClient(), rewriter);

        var mediaSource = source.OpenLiveStreamAsync("144", System.Threading.CancellationToken.None).GetAwaiter().GetResult();

        Assert.StartsWith("/JioTv/manifest.m3u8?auth=", mediaSource.Path);
        Assert.Contains("channel_key_id=144", mediaSource.Path);
        // path decrypts back to a real upstream URL
        var auth = mediaSource.Path.Split("auth=")[1].Split('&')[0];
        var upstream = cipher.Decrypt(auth);
        Assert.StartsWith("https://", upstream);
        Assert.EndsWith(".m3u8", upstream);
        Assert.Equal("hls", mediaSource.Container);
        Assert.True(mediaSource.SupportsDirectPlay);
    }
}

/// <summary>Shim combining test JioTvClient with fake handler transport resolution.</summary>
public static class TuningTestHelpers
{
    /// <summary>Creates a client whose Playable URL factory routes through mocked handler with fixed content.</summary>

}
