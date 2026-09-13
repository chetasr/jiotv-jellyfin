# JioTV Jellyfin Tuner Plugin — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Self-contained Jellyfin Live TV tuner plugin streaming JioTV live channels from the user's own Jio account using Jio's Android app API.

**Architecture:** Plugin library loaded by Jellyfin exposing a native `ITunerHost` (channel list + stream notifications) plus ASP.NET-style proxy endpoints that fetch, rewrite, and self-heal Jio's HLS manifests and segments. All auth (OTP login, JWT-aware token refresh, `__hdnea__` cookie persistence) lives inside the plugin.

**Tech Stack:** C# / .NET 8, Jellyfin.Plugin SDK (Jellyfin.Model + Jellyfin.Controller), standard `HttpClient`, System.Text.Json, xUnit.

**Spec:** `docs/spec.md` (in same directory as this plan)

## Global Constraints
- Jellyfin 10.9+ / .NET 8 plugin layout, standard `Directory.Build.props` + `xUnit` test project.
- Every JioTV API response manual field access goes through a single typed `JioTvClient` API surface — no raw fetches scattered per-file.
- All appkeys/srno/headers live in one static `JioConstants.cs` ported from JioTV Go `internal/constants/*.go` so they can be patched in one place if Jio rotates versions.
- All files from project root `~/jiotv-jellyfin/`.
- Repo commits after each Task — on "DONE" step of each task.

---

## File Structure

```
JioTv.Plugin/
  JioTv.Plugin.csproj
  JioTvPlugin.cs            (entry: IHasTaskUtilities, registers services)
  Tuning/
    JioTunerHost.cs         (ITunerHost impl: channels + defaults)
    JioChannel.cs           (Channel enumeration model)
  Auth/
    JioAuth.cs              (OTP login, token refresh, mutex, JWT expiry)
    CredentialStore.cs      (persist creds JSON to plugin data dir)
    JioOtpSessions.cs       (in-memory state between OTP send & verify)
  Network/
    JioConstants.cs         (headers, urls, appkey — from JioTV Go)
    JioHttp.cs              (HttpClient wrapper with app headers)
    JioTvClient.cs          (playback geturl, channel list, EPG)
  Streaming/
    SecureUrl.cs            (AES encrypt/decrypt of auth= params — port of pkg/secureurl)
    HdneaCache.cs           (per-channel CDN token cache + dead-channel cache)
    ManifestRewriter.cs     (master m3u8 → proxy URLs)
    HlsProxyHandler.cs      (manifest/key/segment HTTP endpoints, self-heal logic)
    ProxyRoutes.cs          (route registration)
  Config/
    PluginConfigPage.cs     (login flow UI)
  Epg/
    JioEpg.cs               (EPG endpoint + parser to Jellyfin GuideInfo)
JioTv.Tests/
  JioTv.Tests.csproj
  SecureUrlTests.cs
  HdneaCacheTests.cs
  ManifestRewriterTests.cs
  ManualJioContractTests.cs   (skipped-by-default live-API integration)
```

---

## Task 1: Skeleton + Plugin Manifest Loading

**Files:**
- Create: `JioTv.Plugin/JioTv.Plugin.csproj`, `JioTv.Plugin/JioTvPlugin.cs`, solution-level `Directory.Build.props`
- Test: skeleton build passes; no behavioral test yet.

**Interfaces:**
- Produces: `public JioTvPlugin : IPlugin` informing Jellyfin of `Name="JioTV Wizard"`; `JioTvPlugin` singleton instance will be resolved by all later tuners.

1. [ ] Scaffold repo: `git init`, csproj targeting `net8.0` with reference to `Jellyfin.Model` and `Jellyfin.Controller` (NuGet `Jellyfin.Plugin.SDK`-style packages per 10.9 docs), xUnit test project.
2. [ ] Minimal `JioTvPlugin` with `Guid.Empty` plugin GUID (assign a fresh UUID).
3. [ ] `dotnet build` green; commit DONE marker.

---

## Task 2: Port `JioConstants` + `JioHttp`

**Files:**
- Create: `JioTv.Plugin/Network/JioConstants.cs`, `JioTv.Plugin/Network/JioHttp.cs`
- Reference: `/tmp/jiotv_go/internal/constants/urls/urls.go`, `.../headers/headers.go`

**Interfaces:**
- Produces: `static class JioConstants { PlaybackUrl, LoginOtpUrl, RefreshAccessTokenUrl, RefreshSsoTokenUrl, GetChannelsUrl... }` and `JioHttp { HttpClient Build(); HttpRequestMessage Authed(request) }`.
- Consumes: none.

1. [ ] Port all application headers, URLs, appkey values verbatim from JioTV Go constants into `JioConstants`.
2. [ ] `JioHttp` with a single `HttpClient` instance (default timeout 10 s, follow redirects true).
3. [ ] Unit test: a smoke test asserting constants match JioTV Go's exact string values (copy literals from the Go files as fixtures into the test).
4. [ ] Commit.

---

## Task 3: OTP Login + CredentialStore (port `internal/handlers/auth.go` + `pkg/utils/credentials.go`)

**Files:**
- Create: `JioTv.Plugin/Auth/JioOtpSessions.cs`, `JioTv.Plugin/Auth/CredentialStore.cs`, `JioTv.Plugin/Auth/JioAuth.cs`
- Test: `JioTv.Tests/Auth/JioAuthTests.cs`

**Interfaces:**
- Produces: `JioAuth.SendOtpAsync(string mobileNumber)`, `JioAuth.VerifyOtpAsync(mobile, otp)`, `JioAuth.EnsureFreshTokensAsync()` returning `JioCredentials`.
- Consumes: `JioHttp`, `CredentialStore`.
- Produces disk format: `~/.jio/jiotv_go.credentials.json`-equivalent in Jellyfin plugin data dir.

1. [ ] Port `sendOtp` / `verifyOtp` / `refresh` request/response JSON shapes from Go (`internal/handlers/auth.go`, `internal/handlers/token_validity.go`).
2. [ ] Write failing test for refresh decision: `IsAccessTokenShouldRefresh(lastSeen, jwtExpiry)`, ported from `token_validity.go`.
3. [ ] Implement `JioAuth`, thread-safety around `tokenRefresh` same as mutex in Go.
4. [ ] `CredentialStore.Save/Load` round-trip test.
5. [ ] Commit.

---

## Task 4: JioTvClient (channel list + playback geturl)

**Files:**
- Create: `JioTv.Plugin/Network/JioTvClient.cs`, model records
- Reference: `/tmp/jiotv_go/pkg/television/television.go` (Live method), `AllLiveChannels` / `tv.live()`.

**Interfaces:**
- Produces: `JioTvClient.GetChannelsAsync() → List<Channel>` and `JioTvClient.GetPlaybackUrlAsync(channelId, quality)` returning internal `LiveResult { manifestUrl, hdneaToken }`.
- Consumes: `JioHttp`, `JioAuth.EnsureFreshTokensAsync`.

1. [ ] Add typed models for Jio playback response.
2. [ ] `GetChannelsAsync` port of Go's channel list + `GetAllChannels` behavior (JioTV Go's `television.go` has json shapes to port verbatim).
3. [ ] Contract tests marked `[Trait("Category","ManualAPI")]` — can be skipped locally, run only with a real Jio account configured.
4. [ ] Commit.

---

## Task 5: SecureUrl (AES-encrypt internal manifest URLs)

**Files:**
- Create `JioTv.Plugin/Streaming/SecureUrl.cs`
- Test: `JioTv.Tests/SecureUrlTests.cs` — port patterns from `pkg/secureurl/secureurl.go` and `.../secureurl_test.go`.

**Interfaces:**
- Produces: `SecureUrl { string Encrypt(string url); string EncryptDeterministic(string url); string TryDecrypt(string token, out string url); }` — behavior-identical to Go implementation (AES-256-CBC with IV prefix, URL-safe Base64).

1. [ ] Port Go `secureurl.go` including test vectors.
2. [ ] Byte-compatibility test — input Go fixture encrypted with Go tool, read exactly same into C#.
3. [ ] Commit.

---

## Task 6: HdneaCache + ManifestRewriter

**Files:**
- Create: `JioTv.Plugin/Streaming/HdneaCache.cs`, `JioTv.Plugin/Streaming/ManifestRewriter.cs`
- Test: `HdneaCacheTests.cs`, `ManifestRewriterTests.cs`

**Interfaces:**
- Produces: `HdneaCache { TryGetCached(key), Set(key, ttl), Clear(key), RecentlyDead(channelId) }` port of `hdneaRemainingLifetime`, `hdneaCacheKey` etc. from `internal/handlers/handlers.go`.
- Produces: `ManifestRewriter { string Rewrite(string masterManifest, string channelId, string authParam, bool useAbsoluteUrls) }` — takes Jio master m3u8 and rewrites upstream variant URLs to `/JioTv/manifest.m3u8?auth=...`-style plugin URLs.

1. [ ] Port token cache from JioTV Go using `IMemoryCache` (Microsoft.Extensions.Caching.Memory).
2. [ ] Write manifest parser tests using fixture playlist text (copy sample m3u8 text from Go `internal/handlers/handlers_test.go`).
3. [ ] Implement `Rewrite` with `SecureUrl.EncryptDeterministic` so rewrites stay stable across requests.
4. [ ] Commit.

---

## Task 7: HlsProxyHandler (self-heal streaming core)

**Files:**
- Create: `JioTv.Plugin/Streaming/HlsProxyHandler.cs`, `JioTv.Plugin/Streaming/ProxyRoutes.cs`
- Reference: port of `internal/handlers/handlers.go` `RenderHandler` + `LiveHandler` + `token_validity.go`.

**Interfaces:**
- Produces: ASP.NET endpoints registered through `Jellyfin BaseController`-style API surface exposed by `JioTvPlugin`:
  - `GET /JioTv/manifest.m3u8?auth={enc}&ch={id}&q={q}`
  - `GET /JioTv/key.m3u8?...` (variant variant index)
  - `GET /JioTv/parts.ts?...` (keyframe extension)
- Consumes: `SecureUrl`, `HdneaCache`, `JioTvClient`, `JioAuth`.

1. [ ] Port self-heal decision tree from `RenderHandler` (401/403/404 → refresh token → retry → optionally quality ladder fallback, respecting `isChannelRecentlyDead`).
2. [ ] Test with mocked Jio responses: arbitrary 200 m3u8 fixture, forced 403, forced 404 — assert retry behavior and emit same final body.
3. [ ] Commit.

---

## Task 8: Tune `ITunerHost`

**Files:**
- Create: `JioTv.Plugin/Tuning/JioTunerHost.cs`, `JioTv.Plugin/Tuning/JioChannel.cs`
- Reference: check Jellyfin's own `Jellyfin.LiveTv.TunerHosts` (e.g. `M3UTunerHost` in `Jellyfin/Jellyfin` repo `MediaBrowser.LiveTv.TunerHosts`) copy/paste shape.

**Interfaces:**
- Consumes: `JioTvClient.GetChannelsAsync`, `JioAuth`.
- Produces: tuner host that enumerates channels; exposes `int ChannelCount` etc.; exposes `OpenLiveStream` that rewrites to ProxyRoutes URL and returns a `MediaSourceInfo` pointing at the plugin's own proxy endpoint that Jellyfin itself streams from.

1. [ ] Read actual Jellyfin source for `ITunerHost` contract first (`MediaBrowser.Controller.LiveTv`) and note required members + behaviors.
2. [ ] Implement channel enumeration service + live stream opening.
3. [ ] Test: fake credentials document; integration test run once against a real Jellyfin 10.9 dev instance (plugin loaded from plugin dir), verify channels appear in Live TV guide.
4. [ ] Commit.

---

## Task 9: EPG pipeline

**Files:**
- Create: `JioTv.Plugin/Epg/JioEpg.cs`
- Reference: `pkg/epg/*.go` (compressed XML storage), `internal/handlers/epg.go` (serverDate IST semantics).

**Interfaces:**
- Produces: `JioEpg.GetChannelsEpgAsync(channelIds)` returning `Dictionary<string, List<GuideInfo>>` for Jellyfin Live TV schedule refresh.

1. [ ] Port logger-only epg fetch code from JioTV Go, converting to plugin's channel models.
2. [ ] Cache "epg.xml.gz" file in plugin data dir using actual JioTV cache TLL/refresh pattern from Go epg scheduler (`pkg/scheduler/*`).
3. [ ] Test parser with sample XML fixture (copy Go fixtures).
4. [ ] Commit.

---

## Task 10: Config page + login flow

**Files:**
- Create: `JioTv.Plugin/Config/PluginConfigPage.cs` (admin UI HTML/JS as plugin config page)
- Test: manual / screenshot once Jellyfin dashboard shows the page.

**Interfaces:**
- Consumes: `JioAuth.SendOtpAsync` / `VerifyOtpAsync`, `CredentialStore`.

1. [ ] Config page with fields: mobile number input, "Send OTP", OTP input, "Verify".
2. [ ] Show account name/plan type & token expiry countdown from creds and JWT.
3. [ ] Logout button (clears credentials).
4. [ ] Manual test against dev Jellyfin.
5. [ ] Commit.

---

## Task 11: DASH/DRM + catch-up — explicitly deferred

Document in `docs/NOTES.md` that DRM (`drm.go`) and catch-up are out of scope for v1; do not implement; write pointers to Go source locations for a future v2.

---

## Task 12: Final integration testing

**Files:**
- Create: `README.md`, `docs/KNOWN_ISSUES.md`

1. [ ] Full end-to-end guide + playback on Jellyfin 10.9 with a real Jio account.
2. [ ] Verify stream survives long idle (> 40 min = one token lifecycle).
3. [ ] Verify no 403s reach the player UI in main player client paths.
4. [ ] Self-review pass using code-review skill; commit.

---

## First "explain-the-plan-back" sanity gate
Before writing code, verify the Implementer can explain: (a) why we use plugin proxy endpoints instead of returning raw Jio URLs (auth headers + self-heal), (b) what gets cached in `HdneaCache`, (c) what makes this differ from simply formatting an M3U. If they can't, have them read the spec again.
