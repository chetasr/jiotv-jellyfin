using JioTv.Plugin.Network;
using Xunit;

/// <summary>
/// Fixtures ported verbatim from JioTV Go internal/constants (urls.go, headers.go).
/// These lock our ported constants to the exact strings JioTV Go ships; if JioTV Go
/// rotates an appkey/versionCode, update fixtures and constants together.
/// </summary>
public static class JioTvGoFixtures
{
    // --- internal/constants/urls/urls.go ---
    public const string PlaybackApiPath = "/playback/apis/v1.1/geturl?langId=6";
    public const string JioTvApiDomain = "jiotvapi.media.jio.com";
    public const string TvMediaDomain = "tv.media.jio.com";
    public const string JioTvCdnDomain = "jiotvapi.cdn.jio.com";
    public const string AuthMediaDomain = "auth.media.jio.com";

    public const string RefreshTokenUrl = "https://auth.media.jio.com/tokenservice/apis/v1/refreshtoken?langId=6";
    public const string RefreshSsoTokenUrl = "https://tv.media.jio.com/apis/v2.0/loginotp/refresh?langId=6";

    public const string ChannelsApiUrl = "https://jiotvapi.cdn.jio.com/apis/v3.1/getMobileChannelList/get/?langId=6&os=android&devicetype=phone&usertype=JIO&version=315&langId=6";
    public const string EpgUrlFormat = "https://jiotv.data.cdn.jio.com/apis/v1.3/getepg/get?offset={0}&channel_id={1}";

    // --- internal/constants/headers/headers.go ---
    public const string UserAgentOkHttp = "okhttp/4.12.0";
    public const string UserAgentPlayTv = "plaYtv/7.1.8 (Linux;Android 8.1.0) ExoPlayerLib/2.11.7";
    public const string VersionCode422 = "422";
    public const string ApiKeyJio = "l7xx938b6684ee9e4bbe8831a9a682b8e19f";
    public const string UserGroup = "tvYR7NSNn7rymo3F";
    public const string ContentTypeJson = "application/json";
}

namespace JioTv.Tests
{
    /// <summary>
    /// Locks JioConstants to the exact strings JioTV Go ships.
    /// </summary>
    public class JioConstantsTests
    {
        [Theory]
        [MemberData(nameof(UrlCases))]
        public void Url_MatchesJioTvGo(string actual, string expected) => Assert.Equal(expected, actual);

        [Theory]
        [MemberData(nameof(HeaderCases))]
        public void Header_MatchesJioTvGo(string actual, string expected) => Assert.Equal(expected, actual);

        public static TheoryData<string, string> UrlCases => new()
        {
            { JioConstants.PlaybackApiPath, JioTvGoFixtures.PlaybackApiPath },
            { JioConstants.JioTvApiDomain, JioTvGoFixtures.JioTvApiDomain },
            { JioConstants.TvMediaDomain, JioTvGoFixtures.TvMediaDomain },
            { JioConstants.JioTvCdnDomain, JioTvGoFixtures.JioTvCdnDomain },
            { JioConstants.AuthMediaDomain, JioTvGoFixtures.AuthMediaDomain },
            { JioConstants.RefreshTokenUrl, JioTvGoFixtures.RefreshTokenUrl },
            { JioConstants.RefreshSsoTokenUrl, JioTvGoFixtures.RefreshSsoTokenUrl },
            { JioConstants.ChannelsApiUrl, JioTvGoFixtures.ChannelsApiUrl },
            { JioConstants.EpgUrlFormat, JioTvGoFixtures.EpgUrlFormat },
        };

        public static TheoryData<string, string> HeaderCases => new()
        {
            { JioConstants.UserAgentOkHttp, JioTvGoFixtures.UserAgentOkHttp },
            { JioConstants.UserAgentPlayTv, JioTvGoFixtures.UserAgentPlayTv },
            { JioConstants.VersionCode, JioTvGoFixtures.VersionCode422 },
            { JioConstants.ApiKeyJio, JioTvGoFixtures.ApiKeyJio },
            { JioConstants.UserGroup, JioTvGoFixtures.UserGroup },
            { JioConstants.ContentTypeJson, JioTvGoFixtures.ContentTypeJson },
        };
    }
}
