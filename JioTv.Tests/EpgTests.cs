using System;
using System.Xml;
using System.Xml.Serialization;
using JioTv.Plugin.Epg;
using Xunit;

namespace JioTv.Tests;

/// <summary>
/// EPG pipework tests — port of JioTV Go's pkg/epg/epg_test.go cases that
/// test programme creation, XML serialization, timestamp formatting, and
/// the refresh-freshness policy.
/// </summary>
public class EpgTests
{
    [Fact]
    public void Programme_CarriesXmlAttributes()
    {
        var programme = EpgModel.NewProgramme(
            channelId: 123,
            start: "20231225120000 +0530",
            stop: "20231225130000 +0530",
            title: "Test Show",
            desc: "Test Description",
            category: "Entertainment",
            iconSrc: "test_icon.jpg");

        Assert.Equal("123", programme.Channel);
        Assert.Equal("Test Description", programme.Desc);
        Assert.EndsWith("/test_icon.jpg", programme.IconSrc); // poster URL base prepended
    }

    [Fact]
    public void FormatTime_MatchesGoConvention()
    {
        var t = new DateTimeOffset(2023, 12, 25, 12, 0, 0, TimeSpan.FromMinutes(330));
        Assert.Equal("20231225120000 +0530", EpgModel.FormatTime(t));
    }

    [Fact]
    public void GenerateXml_IncludesChannelAndProgrammes()
    {
        var xml = EpgModel.GenXml(new[]
        {
            new EpgChannel { Id = 123, Display = "Test Channel" },
        }, new[]
        {
            EpgModel.NewProgramme(123, "20231225120000 +0530", "20231225130000 +0530", "Show", "Desc", "News", iconSrc: string.Empty),
        });

        Assert.Contains("version=\"1.0\"", xml, StringComparison.Ordinal);
        Assert.Contains("channel id=\"123\"", xml, StringComparison.Ordinal);
        Assert.Contains("<display-name>Test Channel</display-name>", xml, StringComparison.Ordinal);
        Assert.Contains("programme channel=\"123\"", xml, StringComparison.Ordinal);
        Assert.Contains("start=\"20231225120000", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void GenXMLGz_WritesGzipFile()
    {
        var xml = EpgModel.GenXml(
            new[] { new EpgChannel { Id = 1, Display = "C1" } },
            Array.Empty<EpgProgramme>());

        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "jiotv-gzip-" + Guid.NewGuid().ToString("N") + ".xml.gz");
        EpgModel.GzipWrite(xml, path);

        // roundtrip via gzip
        var read = EpgModel.GzipRead(path);
        Assert.Equal(xml, read);
        System.IO.File.Delete(path);
    }

    [Theory]
    [InlineData("2024-01-01", "2024-01-01", false)]
    [InlineData("2024-01-01", "2024-01-02", true)]
    [InlineData("", "2024-01-02", true)]
    public void ShouldRegenerate_FollowsSameDatePolicy(string fileDate, string today, bool expected)
    {
        Assert.Equal(expected, EpgCacheService.ShouldRegenerate(fileDate, today));
    }
}
