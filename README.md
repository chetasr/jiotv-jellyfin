# Jellyfin JioTV Plugin 📺

A **native Jellyfin Live TV tuner plugin** (Jellyfin 10.9+ / Jellyfin 12) that streams JioTV live channels to Jellyfin using **your own Jio subscription**. Written in C# as a port of [JioTV Go](https://github.com/JioTV-Go/jiotv_go)'s streaming flow, with all proxying, token lifecycle and AES-encrypted internal URLs handled inside the plugin — no sidecars, no extra processes, no manual M3U juggling.

```
┌────────────────────┐            ┌──────────────────────────┐
│   Jellyfin Server  │ ←──────  ┌─│ Jellyfin JioTV plugin    │
│  Live TV guides    │ ─ tuner ─│ + Jio OTP login / tokens  │→ Jio TV APIs + CDN
└────────────────────┘            └──────────────────────────┘
```

## ✅ Features

- **Native tuner host** — 1100+ JioTV channels appear directly in Jellyfin's channel list / Live TV guide.
- **Self-healing HLS proxy inside the plugin** — the plugin serves re-written manifests whose variants / segments / AES keys point at the plugin's own `/JioTv/*` endpoints. This path silently rotates the `__hdnea__` Akamai token, retries on 401/403/404, and walks the quality ladder as the upstream CDN expires links mid-play.
- **Full token lifecycle** — OTP login + auto-refresh of both `accessToken` (via refresh-token API) and `ssoToken` (via the SSOToken refresh endpoint) — long-running playback doesn't need a re-login.
- **AES-encrypted proxy URLs** — the `?auth=` parameters in generated `m3u8` manifests are AES-256-CTR encrypted with a random server key, protected from token tamper and URL walking.
- **Self-heal load balancing** — on 401/403/404 the plugin quietly re-fetches a new playback URL, harvests the fresh `__hdnea__` cookie and transparently retries the same URL so the player never sees errors.
- **Self-contained** — no external processes; the plugin *is* the proxy server. Nothing to install apart from the standard Jellyfin build.

## 🚀 Getting Started

1. **Install the plugin.** Place the compiled `JioTv.Plugin.dll` (+ `JioTv.Plugin.deps.json`) into Jellyfin's `plugins/JioTv` folder, then restart Jellyfin.
2. **Login.** Open **Dashboard → Plugins → JioTV**, enter your Jio mobile number, click **Send OTP**, type the code from SMS, then **Verify & log in**.
3. **Open Live TV.** The tuner enumerates all JioTV channels (≈1100+); pick one and play. That's it.

You won't need to log in again after each restart — the plugin persists its credentials.

### Installation

1. Grab the plugin DLL (`JioTv.Plugin.dll` + supporting `.deps.json`) from a GitHub release **or** `dotnet build` this repo.
2. Copy the output files into Jellyfin's plugin directory:
    - Docker: into the mounted path you exposed as `/config/plugins/JioTv/`
    - Bare metal: `~/.local/share/jellyfin/plugins/JioTv/`
3. Restart Jellyfin.

### Login flow

```
 ┌─ Dashboard → Plugins → JioTV ───────────────────────────────────┐
 │  📺 JioTV                              Send OTP ▸              │
 │                            [0 1 2 3 4 5 6 7 8 9]     4 digits   │
 │  SMS: "OTP from JioTV …  391479"  ▸ paste it → Verify           │
 │                                                                 │
 │  Logged in: mobile ending 5330        🔑 token until 12-09 …   │
 └──────────────────────────────────────────────────────────────────┘
```

On success, the plugin stores credentials in its config folder — `jiotv_credentials.json` — along with the AES key used for proxy URLs (`secure_url_key.bin`) and a per-day EPG cache (`epg.xml.gz`).

### Requirements

- **Jellyfin 10.9+** or Jellyfin 12 (tested with 12.0.0).
- An **active Jio mobile subscription** — the plugin uses your Jio account exactly how the JioTV Android app does. Premium content requires your plan to allow it (the plugin surfaces Jio's `No eligible plans found` for channels beyond your plan).
- Outbound network access to:
  - `jiotvapi.media.jio.com` (playback / channel / OTP APIs)
  - `auth.media.jio.com` (token refresh)
  - `tv.media.jio.com` (AES key fetch)
  - `jiotvbpkmob.cdn.jio.com` / `jiotvmblive.cdn.jio.com` (manifest + segment fetch)

## 🧭 How streaming works (architecture)

```
Player (Kodi / browser / Jellyfin Web)
  │   HLS request
  ▼
/JioTv/manifest.m3u8?auth=<AES>…  ←— Jellyfin tuner's proxy endpoint
  │   encrypted auth decrypted with AES-256-CTR key
  ▼
Jio playback API    ──▶  master m3u8 fetched
  │                      self-heal cached __hdnea__ token
  ▼
/JioTv/manifest.m3u8?auth=… (variants)
  │
  ▼
/JioTv/segment.ts?auth=…  → fresh segments
/JioTv/key?auth=…         → AES-128 key (16-byte)
                   Upstream auth: appkey, deviceId, ssotoken, crmid,
                   usergroup, __hdnea__, PlayTV useragent, etc.
```

The **`HdneaCache`** keeps per-channel Akamai tokens for ~60s, tightly matching expiration, dead-channel marking and re-warm. If a CDN request 401s/403s/404s, the plugin transparently refreshes with a recovered token or restarts the timeline at a different bitrate — the player never notices.

## 📁 Layout

```
JioTv.Plugin/
├── JioTunerHost.cs        # Native tuner host
├── JioTvChannelMapper.cs  # Channel → Jellyfin ChannelInfo
├── JioAuth.cs             # OTP login, token refresh, mutex, SSO
├── CredentialStore.cs     # AES-compatible on-disk JSON (Go file format compatible)
├── JioConstants.cs        # All appkey/srno/User-Agent (anti-tamper rotated values)
├── JioTvClient.cs         # Playback API client (playback v1.1)
├── SecureUrl.cs           # AES-CTR (random & deterministic) URL tokenization
├── HdneaCache.cs          # Token + dead-channel caches
├── ManifestRewriter.cs    # m3u8 rewriter
├── HlsProxyRenderer.cs    # The self-heal engine (401/403/404 recovery)
└── JioTvProxyController.cs  # /JioTv/{manifest.m3u8,segment.ts,key,endpoints}

JioTv.Tests/                # xUnit tests (64 passing)
```

## FAQ

### Is this legal / safe?

The plugin talks to JioTV's API **using your own subscription**, impersonating the JioTV Android app. Think of it like the TVHeadend / HD Homerun / IPTV merge — the server presents thumbnails and metadata, and it uses the Jio CDN you'd use normally. Premium channels are *not* DRM-bypassed; playback only works for what your plan enables. We can't help if you don't have a Jio subscription or if Jio rotates their anti-tamper constants.

### Where do my Jio credentials live?

Everything is persisted on disk inside your plugin config directory. To move Jellyfin copies the `configurations/JioTv` folder to the new host, including the same `secure_url_key.bin`, and restart the server.

### Which quality does it pick?

The plugin picks the best bitrates from Jio's returned variant list and falls back through auto/high/medium/low if a variant is missing.

### Something failed with code 3012, "No eligible plans found"?

JioTV's server decided you don't have an active plan for that specific channel on your account. Try another channel. Channels that are included in your plan will always play.

## 📚 Reference

- [JioTV Go](https://github.com/JioTV-Go/jiotv_go) — upstream reference implementation
- [Jellyfin plugin template](https://github.com/jellyfin/jellyfin-plugin-template)
- Older PHP equivalent: [TS-JioTV](https://github.com/mitthu786/TS-JioTV)

## ⚠️ Disclaimer

This project is for educational purpose. JioTV is a product of Reliance Jio Infocomm Ltd. This is not affiliated with, endorsed, or supported by Reliance. Use at your own discretion and **respect content rights**.
