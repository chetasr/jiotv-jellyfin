namespace JioTv.Plugin.Network;

/// <summary>
/// All JioTV API request/response constants, ported verbatim from JioTV Go's
/// internal/constants package (urls.go + headers.go). These values are
/// anti-tamper-sensitive and rotate with JioTV app releases — keep them in
/// sync with JioTV Go so updates can be lifted on rotation.
/// </summary>
public static class JioConstants
{
    // --- Domains (urls.go) ---
    public const string JioTvApiDomain = "jiotvapi.media.jio.com";
    public const string TvMediaDomain = "tv.media.jio.com";
    public const string JioTvCdnDomain = "jiotvapi.cdn.jio.com";
    public const string AuthMediaDomain = "auth.media.jio.com";
    public const string JioTvDataCdnDomain = "jiotv.data.cdn.jio.com";
    public const string JioTvCatchupCdnDomain = "jiotv.catchup.cdn.jio.com";

    // --- Authentication URLs (urls.go) ---
    public const string RefreshTokenUrl = "https://auth.media.jio.com/tokenservice/apis/v1/refreshtoken?langId=6";
    public const string RefreshSsoTokenUrl = "https://tv.media.jio.com/apis/v2.0/loginotp/refresh?langId=6";

    // --- Channel listing URLs (urls.go) ---
    public const string ChannelsApiUrl = "https://jiotvapi.cdn.jio.com/apis/v3.1/getMobileChannelList/get/?langId=6&os=android&devicetype=phone&usertype=JIO&version=315&langId=6";

    // --- EPG URLs (urls.go) ---
    public const string EpgUrlFormat = "https://jiotv.data.cdn.jio.com/apis/v1.3/getepg/get?offset={0}&channel_id={1}";
    public const string EpgPosterUrl = "https://jiotv.catchup.cdn.jio.com/dare_images/shows";

    // --- Playback (urls.go) ---
    public const string PlaybackApiPath = "/playback/apis/v1.1/geturl?langId=6";

    // --- User agents (headers.go) ---
    public const string UserAgentOkHttp = "okhttp/4.12.0";
    public const string UserAgentPlayTv = "plaYtv/7.1.8 (Linux;Android 8.1.0) ExoPlayerLib/2.11.7";
    public const string UserAgentGoClient = "JioTV Go (https://github.com/jiotv-go/jiotv_go, version on server)";

    // --- Device/app identity (headers.go) ---
    public const string DeviceTypePhone = "phone";
    public const string OsAndroid = "android";
    public const string VersionCode = "422";
    public const string ApiKeyJio = "l7xx938b6684ee9e4bbe8831a9a682b8e19f";
    public const string UserGroup = "tvYR7NSNn7rymo3F";

    // --- Content types (headers.go) ---
    public const string ContentTypeJson = "application/json";
    public const string ContentTypeFormUrlEncoded = "application/x-www-form-urlencoded";
    public const string AcceptJson = "application/json";
}
