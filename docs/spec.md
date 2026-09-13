# Jellyfin JioTV Plugin — Design Spec

**Goal:** A self-contained Jellyfin Live TV tuner plugin that streams JioTV live channels using the user's own Jio account, ported from JioTV Go.

## Decisions (locked)
- **Self-contained tuner** (not M3U-provider route): plugin implements a native Jellyfin tuner host; no sidecar processes.
- **Base:** port JioTV Go (`internal/handlers/auth.go`, `internal/handlers/handlers.go`, `pkg/television/*.go`) to C#.
- **Language:** C# (.NET 8; Jellyfin 10.9+ targets it).
- **Scope:** live channels only. Catch-up, DASH/DRM channels, and multi-account are explicitly out of scope for v1.
- **EPG:** build directly from Jio's own EPG API (same source as JioTV Go's `epg.xml.gz` pipeline); no external XMLTV dependency.

## Architecture

```
Jellyfin (host)
 └─ JioTunerEntry ── ITunerHost  (channel enumeration)
      │  ├─ CredentialStore        creds JSON + AES-encrypted? (v1: plain JSON in plugin data dir)
      │  ├─ JioAuth                OTP login, JWT-aware token refresh, mutex
      │  ├─ JioTvClient            playback v1.1 geturl, app headers, channel list
      │  ├─ HdneaCache             per-channel __hdnea__ token cache (memory, TTL)
      │  └─ EpgService             Jio EPG -> season/descriptions
      └─ JioHlsStream ── ILiveStream (segment/proxy pump to Jellyfin)
           Reads rewritten manifests, proxies .ts/.m4s through the plugin,
           self-heals on 401/403/404 exactly like JioTV Go's RenderHandler.
```

### How streaming works (mirrors JioTV Go)
1. User picks a channel → Jellyfin asks tuner for a live stream.
2. Plugin calls Jio playback API (`jiotv_api/media/playback/v1.1/geturl`) with app headers + token.
3. Response master m3u8 URL (with embedded `__hdnea__`-auth) is AES-encrypted into an opaque `auth=` param handled by our internal proxy endpoints registered as ASP.NET middleware endpoints in the plugin (`auth=...&ch=...&q=...`).
4. Manifest requests are rewritten: upstream channel-variant URLs → plugin proxy URLs (deterministic AES so rewrites stay stable).
5. Segment key/chunk requests either pass HTTP Range through to Jio CDN or plain GET, applying fresh cookie tokens.
6. On 401/403/404: harvest fresh token from last 200 response, refresh upstream URL, transparently retry (never surface errors to player) exactly like JioTV Go's RenderHandler. Add per-channel short "dead" cache to avoid recovery storms.

### Token lifecycle (from JioTV Go)
- JWT `exp` drives refresh; refresh 5 min ahead; separate mutex prevents double refresh.
- Refresh endpoints `auth.media.jio.com/.../refreshtoken` + `tv.media.jio.com/apis/v2.0/loginotp/refresh` for SSO token.
- Persist credentials + LastTokenRefreshTime JSON on disk.

## API endpoints plugin serves (internal to Jellyfin)
- `GET /tuner/channels` (implemented as tuner host channel enumeration, not REST)
- Internal endpoints (registered via service registrator dynamic routes):
  - `/JioTv/Play.m3u8?auth=...`
  - `/JioTv/manifest.m3u8?auth=...&ch=...&q=...`
  - `/JioTv/key.m3u8?auth=...&ch=...&q=...`
  - `/JioTv/segment.ts?auth=...&ch=...&q=...`
- Config page: login (mobile → OTP), channel count sanity, log.

## Non-goals
- Catch-up/VOD. DRM/DASH channels. Multi-account. Transcoding control beyond quality select.

## Risks / unknowns to verify at first spike
- Exact JioTV API constants (appkey UbZ3b0v8, headers) have drifted across app versions; port with JioTV Go constants initially, expect patching.
- Jellyfin tuner hook points differ 10.8 → 10.9; target 10.9+ stable.
- Anti-tamper/anti-abuse disclaimer: user must own a Jio subscription; personal-use only.
