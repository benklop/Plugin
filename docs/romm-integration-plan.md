# RomM integration plan

Status: proposal. Covers the Moonbase plugin (Jellyfin and Emby), Moonfin Core, Smart-TV and Roku.

## Goal

Let a Moonbase admin point the plugin at a [RomM](https://github.com/rommapp/romm) server so that:

1. **ROMs, BIOS/firmware and all metadata/artwork come from RomM**, not from a folder walk.
2. **Saves (SRAM) and save states sync back to RomM**, per user, so they line up with the user's
   other RomM clients (RomM web player, RetroArch/Grout/handheld sync clients).
3. **RomM's server-side emulator streaming** is a first-class play mode. This is for systems too
   heavy for the client, and for systems like PSX whose disc images are too big to download to a
   TV (Smart-TV caps a ROM at 48 MB, Roku has no local emulator at all).

The plan is based on RomM `master` as of late October 2026 (5.4.x line). RomM's API changes between
minor versions, so the plugin has to read the server version and gate features on it (see
"Version gating").

## What exists today

| Layer | Today |
|---|---|
| Plugin | `GamesService` walks `<Library>/<System>/<Game>/<rom>` on disk. `GamePathResolver` turns absolute paths into opaque base64url tokens. ROM, BIOS, thumbnail/artwork-manifest, core/backend override and save endpoints sit under `/Moonfin/Games/*`. EmulatorJS is served from `/Moonfin/EmulatorJS/*`. Saves are files at `saves/{userId}/{gameId}.{state\|sram\|settings}` (`GameSavesService`). Metadata comes from RDB and LaunchBox. Emby mirrors the Jellyfin code. |
| Moonfin Core | `GamesApi` / `MoonbaseGamesApi` in `server_core`. Two players: the native libretro player, which downloads the ROM to a cache dir (`GameStorage`), and the EmulatorJS WebView. Only `state` and `settings` saves are synced. |
| Smart-TV | `gamesApi.js` + `emulatorjs.js`. EmulatorJS runs in-app, single-threaded cores, 48 MB ROM ceiling. Syncs `state` and `settings` only. |
| Roku | Detects game libraries by name for a glyph. No browse or play. |

Two facts shape the design:

- **Every client already talks only to `/Moonfin/Games/*`.** If the plugin can serve RomM content
  through the same contract, all clients get RomM browse, metadata and local play with almost no
  client work.
- **SRAM is not synced by any client today**, only emulator-specific states. RomM, and every other
  RomM sync client, treats the in-game save file (`.srm` and friends) as the portable unit. So save
  sync to RomM needs client work no matter where the integration lives.

## RomM surface we depend on

All under `/api`. Auth is a Bearer token: a RomM client API token (`rmm_…`) or an OAuth token.

| Purpose | Endpoint (scope) |
|---|---|
| Platforms → Moonfin "systems" | `GET /platforms` (`platforms.read`) |
| ROM list / detail / similar | `GET /roms?platform_ids=…` (paged), `GET /roms/{id}`, `/roms/{id}/similar` (`roms.read`) |
| ROM bytes | `GET /roms/{id}/content/{file_name}` with `file_ids=` for one file of a multi-file ROM; supports `Range`. Multi-disc ROMs come back as a zip with a generated `.m3u` unless `file_ids` is given. `GET /roms/{id}/files` lists files with size, hashes and `category`. |
| Covers / screenshots / manuals | `path_cover_small/large`, `url_cover`, `screenshots`, `url_manual` on the ROM schema |
| BIOS | `GET /firmware?platform_id=…`, `GET /firmware/{id}/content/{file_name}` (`firmware.read`) |
| Saves (SRAM) | `GET/POST /saves?rom_id=&emulator=&slot=&device_id=&content_hash=…`, `GET /saves/{id}/content`, `PUT /saves/{id}`. `409` when the device's sync record is stale. (`assets.read/write`) |
| States | `GET/POST /states?rom_id=&emulator=…`, `GET /states/{id}/content`, `PUT /states/{id}` |
| Memory cards (PS1/PS2 etc.) | `/memory-cards` |
| Devices + sync tracking | `POST /devices` (`devices.write`), `POST /saves/{id}/downloaded`, `/saves/{id}/track`, `POST /sync/negotiate` |
| Pairing a user without a password | Device authorization flow: `POST /auth/device/init` returns `user_code` + `device_code`, the user approves on the RomM web UI (QR at `/pair/device`), and polling `POST /auth/device/token` returns a client token bound to a RomM device. |
| Play time | `POST /play-sessions` |
| Streaming | `GET /streaming/config` (which platforms have a container, plus capabilities), `POST /streaming/sessions` (`{rom_id, state_id?, save_id?, memory_card_id?}`, answers 202), `GET /streaming/sessions/{platform}/status` (gives the room `host` URL once launched), `…/heartbeat`, `…/save-state`, `…/load-state`, `…/swap-disc`, `…/save-and-exit`, `DELETE /streaming/sessions/{platform}` |

Streaming itself runs as RomM-managed containers ("webstation" + `romm-broker`). Each container runs
a native emulator (SwanStation for PSX, PCSX2, Dolphin, Flycast, RetroArch cores, …) and streams it
to a **browser** over WebRTC via a Selkies web UI. The room URL is that Selkies page.

## Architecture decision: the plugin is the RomM adapter

There are two places the integration could live:

- **A. In the plugin (recommended).** Add a "game source" abstraction behind the existing
  `/Moonfin/Games/*` API, with today's filesystem walk as one source and RomM as another. Clients
  keep one API, Roku and Smart-TV get RomM for free, RomM credentials never leave the server, and
  ROM bytes, saves and streaming control are proxied through the Jellyfin/Emby auth the clients
  already hold.
- **B. In each client.** Every client gets a second `GamesApi` implementation that talks straight
  to RomM. This triples the work (Dart, JS, BrightScript), each TV needs its own RomM login, and it
  only pays off for a "RomM without Jellyfin" mode.

Go with **A**. Keep the Core `GamesApi` interface clean enough that a direct `RommGamesApi` (B)
could be added later for Core alone, if a Jellyfin-less use case ever matters.

```
 Moonfin client ──(Jellyfin/Emby auth)──▶ Moonbase plugin ──(RomM client token, per user)──▶ RomM API
                                              │                                                 │
                                              │  proxied ROM/BIOS bytes, saves, artwork         │
                                              │  streaming control (claim/heartbeat/exit)       ▼
                                              └──(optional reverse proxy of room)──▶ RomM streaming container (Selkies/WebRTC)
```

## Workstreams

### 1. Plugin: game source abstraction (foundation)

- Introduce `IGameSource` with the operations the controllers already need: `GetSystems`,
  `GetGames`, `GetGame`, `OpenRom(range)`, `OpenBios`, `GetArtwork`, plus save operations
  (below). Move the current folder logic into `FileSystemGameSource` without changing behavior.
  This refactor alone is worth doing first and should land with the existing tests green.
- A **RomM library** is a virtual game library defined in plugin config (URL, display name,
  optional platform allow-list), shown next to the real Jellyfin libraries in
  `GET /Moonfin/Games/Libraries`. Library id is stable, e.g. `romm-<hash of URL>`.
- **IDs:** game ids for RomM content are `r<romId>` (and `r<romId>.f<fileId>` where a file must be
  addressed), never paths. `GamePathResolver` keeps handling filesystem tokens only. Since save
  keys embed the game id, a RomM id must never collide with a base64url path token. The `r` prefix
  plus a check in the resolver handles that.
- Put the RomM HTTP client and DTOs in `shared/Romm` (same pattern as `shared/Seerr`), so Jellyfin
  and Emby share them. That covers paging, Range passthrough, version detection, typed errors for
  409/428 and the scope list.

### 2. Plugin: accounts and auth

RomM saves are per RomM user, so each Jellyfin/Emby user needs a RomM identity.

- **Per-user link (default):** a "Link RomM account" action in the client settings. The plugin
  starts the RomM device flow on the user's behalf. The client shows the `user_code` and a QR code
  (TV-friendly, no typing), and the user approves on their phone. The plugin polls `/auth/device/token` and
  stores the resulting client token **encrypted, keyed by Jellyfin user id**. This creates one
  RomM device per Moonfin user per client device (see sync below), and the user can revoke it in
  RomM.
- **Shared admin token (fallback):** the admin enters one RomM token. Browse and play work for
  everyone, but saves are either disabled or stored on the Moonbase side (today's
  `GameSavesService`) and never pushed to RomM. That way one RomM user's saves are never mixed
  across Jellyfin users.
- **Optional SSO later:** if both servers use the same OIDC provider, map identities automatically.
  This is out of scope for v1.
- Request the minimum scopes: `roms.read platforms.read firmware.read assets.read assets.write
  devices.read devices.write roms.user.read roms.user.write`.
- Respect RomM visibility: always call RomM *as the linked user*, so hidden ROMs and platforms and
  the `streaming` permission are enforced by RomM itself. Unlinked users fall back to the admin
  token, read-only.

### 3. Plugin: browse, metadata, artwork

- Systems: map RomM `platform.slug` to Moonfin system ids and cores through a new table next to
  `GameSystemCoreResolver` (e.g. `psx → psx/pcsx_rearmed`, `snes → snes`, `ngc → none-local`).
  Platforms with no local core are still listed, but marked stream-only.
- Games: map `RomSchema` to `GameSummary`/`GameDetail` (name, summary, genres, companies,
  franchises, regions, release year, players, rating, alternative names, revision, size). RomM
  already merges IGDB, ScreenScraper, MobyGames, LaunchBox and other sources, so **RDB and LaunchBox
  lookups are skipped for RomM games**.
- Artwork: serve RomM's cover and screenshots through the existing artwork-manifest protocol
  (protocol 2: versioned URLs, revision = RomM `updated_at` hash) and cache them in
  `GameArtworkStore`, so TVs never hit RomM directly. Extend `GameDetail` with optional fields
  clients can adopt over time: `screenshots[]`, `manualUrl`, `videoId`, `achievementsId` (`ra_id`),
  `similar[]`.
- Caching: cache RomM list responses per user for a short time, and refresh when RomM's scan
  completes. RomM has socket events for this, but a periodic poll is simpler for v1.

### 4. Plugin: ROM and BIOS delivery

- `GET /Moonfin/Games/{lib}/Rom/{id}` for a RomM game streams
  `/api/roms/{id}/content/{file}` and passes `Range`, `Content-Length` and `ETag` through. Don't
  buffer. The current 512 MB in-memory extraction limit only applies to filesystem archives.
- Multi-disc (PSX, Saturn, Sega CD …): expose files individually plus a synthesized `.m3u` the
  plugin builds from `/roms/{id}/files`. Native cores load the `.m3u` and fetch discs lazily.
  EmulatorJS gets the zip-with-m3u bundle RomM already generates.
- Size metadata: `GameDetail.sizeBytes` is the sum of the GAME-category files, so clients can decide
  "download vs stream" before they fetch anything (see 6).
- BIOS: map `/firmware` for the game's platform to the existing `GameBios` list, and serve it
  through the `Bios/{id}` endpoint.

### 5. Saves and states sync to RomM

**Save model.** Moonbase keeps its `kind` names but maps them onto RomM:

| Moonbase kind | Today | With RomM |
|---|---|---|
| `sram` | endpoint exists, unused by clients | RomM **save** (`/api/saves`), `emulator` = RomM emulator folder name for the core, e.g. `retroarch-snes9x` / `pcsx_rearmed`. This is the cross-client-portable unit. |
| `state` | used, key `ejs-{core}-{gameId}` | RomM **state** (`/api/states`), `emulator` = the same core namespace, so a state is only offered to a matching core |
| `settings` | used | stays in Moonbase. It is Moonfin UI and controller config, not game data. |

Work items:

- **Plugin:** a `RommSaveStore` behind `GameSavesService`'s interface. For RomM games and linked
  users, GET/PUT go to RomM. Otherwise behavior stays local. Pass `device_id` (the client's RomM
  device) and `content_hash` on every upload, and set `autocleanup` with a small limit for states.
  A `409` (stale device) goes back to the client as `409` with the server copy's metadata (updated
  time, device name, screenshot URL), so the client can ask "Keep server save / Overwrite with
  this one".
- **Plugin:** new list endpoint `GET /Moonfin/Games/Saves/{gameId}/List`, so the clients can offer
  slot pickers backed by RomM's multiple states and saves (with screenshots), instead of today's
  single blob.
- **Core (native libretro player):** read and write SRAM via `retro_get_memory_data(RETRO_MEMORY_SAVE_RAM)`.
  Load it before the first frame, then push it on exit, on pause/background, and on a debounced
  timer while dirty. Keep the local copy as the offline fallback and reconcile on next launch.
- **Core (EmulatorJS WebView) and Smart-TV:** use EmulatorJS's save-file hooks
  (`gameManager.getSaveFile()` / `loadSaveFiles()`) through `moonfin-bridge.js` to sync SRAM with the
  same triggers. The existing `loadGameStateWithMigration` key scheme stays for filesystem libraries.
- **Compatibility:** to resume a state made by RomM's web player (EmulatorJS) or by RetroArch on a
  handheld, the core *and* close core versions must match. Treat states as "same-core only" and
  show the core name on each slot. SRAM generally carries across cores of the same system and is
  what should be relied on.
- **Play time:** post a `/play-sessions` entry on exit. It's cheap and makes RomM's "continue
  playing" and stats reflect Moonfin play.

### 6. Choosing the play mode: local vs streamed

Add a play-mode decision that every client applies the same way. The server says what is possible,
and the client applies its own constraints.

`GameDetail` gains:

```jsonc
"playModes": {
  "local":  { "available": true,  "core": "pcsx_rearmed", "downloadBytes": 734003200 },
  "stream": { "available": true,  "emulator": "SwanStation", "capabilities": { "liveStates": false, "discSwap": false, "memoryCards": false } }
}
```

Client rule, in order:

1. Use the user's per-game override if set (extends today's `Backend` override with `stream`).
2. Use local if a core exists on this device **and** `downloadBytes` fits the device budget (Smart-TV:
   the current 48 MB blob ceiling. Core: free cache space with headroom, plus a per-platform setting
   such as "never download disc systems on this device") **and** the platform isn't marked
   heavy for this device class (e.g. PS2/GC/Wii are always stream on TVs).
3. Otherwise stream, if `stream.available`.
4. Otherwise show "Can't play on this device" with the reason.

User settings: **Play mode = Auto / Prefer local / Prefer streaming**, plus a per-device
"maximum download size". With this, PSX on a TV streams by default, while a desktop or phone with
space can still download and play it natively.

### 7. Server-side emulation (streaming)

**Plugin endpoints** (thin, authenticated wrappers around RomM's streaming API, called as the
linked user):

- `GET /Moonfin/Games/Streaming/Config` → which platforms stream, and their capabilities.
- `POST /Moonfin/Games/{lib}/Games/{id}/Stream` `{stateId?, saveId?, memoryCardId?}` → claims a
  session. Pass RomM's `409` (all containers busy) and `428` (memory-card import prompt) through
  as structured errors.
- `GET …/Stream/Status` → long-polls RomM's `/sessions/{platform}/status` until `host` is set, so
  clients don't need socket.io. The plugin can listen for RomM's `streaming:launch-ready` socket
  event internally later to cut latency.
- `POST …/Stream/Heartbeat|SaveState|LoadState|SwapDisc|SaveAndExit`, `DELETE …/Stream`.

**Exposing the room safely.** The Selkies room has to be reachable from the client. RomM issue
#3968 points out that this currently means publishing the emulator desktop, and that the host URLs
are visible to any `roms.read` user. Plan:

- v1: the plugin **reverse-proxies the room's HTTP and WebSocket signaling** under
  `/Moonfin/Games/Streaming/Room/{session}/…`, gated by Jellyfin/Emby auth *and* by the session
  belonging to the caller. Only the WebRTC media goes direct (UDP, or TURN for remote play). The
  admin then only exposes Jellyfin plus a TURN server, not the containers. Document the
  TURN requirement for play outside the LAN.
- The same-origin proxy also lets Moonfin inject its own bridge script into the room page:
  map TV remote keys to gamepad input, show an overlay menu (save, load, swap disc, quit), and route
  back/exit to `save-and-exit`. A cross-origin iframe can't be scripted.
- If proxying signaling turns out to be brittle across Selkies versions, fall back to handing the
  client the direct room URL (LAN-only setups) and say so in the admin UI.

**Session lifecycle in clients:**

1. Claim → status poll with a "Starting SwanStation…" screen showing RomM's launch phases (large
   titles unpack first).
2. Open the room in a WebView (Core: reuse the EmulatorJS WebView host, Android TV/desktop/iOS
   WebViews all have WebRTC) or a full-screen iframe (Smart-TV: webOS and Tizen builds with WebRTC.
   Gate by a capability check and fall back to "not supported on this TV").
3. Heartbeat every ~15–30 s while visible. Pause/background → `save-and-exit` after a grace period,
   because containers are scarce. Exit → `save-and-exit` (`wait: true`), then return to details.
4. The resume picker uses RomM states for the container's emulator namespace (from `/streaming/states/{rom_id}`).
   For PSX via SwanStation there are no live slots, only the exit autosave, so the in-game menu
   hides Save/Load state where `supports_live_states` is false and shows just "Save & quit".

**Saves interop with streaming.** A streamed session's saves are written to RomM by the broker
itself. Because they share RomM's save model, a PSX game saved on a streamed session will show up
for local play elsewhere (subject to the core-compatibility caveat above, for SRAM vs memory card
formats). That cross-mode continuity is the main payoff of syncing to RomM rather than to Moonbase.

### 8. Client work by app

**Moonfin Core**
- Models: `playModes`, saves list, streaming DTOs, error types for 409/428.
- `GamesApi`: `streaming*` methods, `listSaves`, SRAM `getSave/putSave(kind: 'sram')` with conflict
  handling.
- Native player: SRAM sync, `.m3u` multi-disc with lazy per-disc download, and a resumable Range
  download into `GameStorage` (PSX images are hundreds of MB).
- Stream player screen: WebView host, launch progress, overlay menu, heartbeat, lifecycle handling.
- Settings: link RomM account (QR flow), play-mode preference, max download size.

**Smart-TV**
- `gamesApi.js`: the same additions. The proxy-for-text vs native-fetch-for-binary split already
  exists and covers SRAM.
- `GamePlayer`: SRAM sync through EmulatorJS hooks.
- New `GameStreamPlayer` view (iframe + remote-key bridge). Disable it via capability detection on TVs
  without WebRTC.
- Details screen: a play button that reflects the chosen mode ("Play" / "Stream").

**Roku**
- No local emulator and no WebRTC, so the realistic v1 is **browse and metadata only**. Add a games
  library UI on the existing `/Moonfin/Games` API, with "Play on another device" and
  continue-playing info from RomM.
- Research spike, not committed: stream to Roku as **LL-HLS** (a transcode of the container's
  output, similar to the unofficial RommStreamServer), with remote input sent over HTTP to the
  plugin. Expected latency is 1–3 s, which only suits turn-based games (much of the PSX RPG
  library). Only pursue it if the spike shows acceptable latency.

### 9. Admin UX (plugin config page)

- RomM tab: URL, admin/fallback token, "Test connection" (shows RomM version, platforms, whether
  streaming is enabled and which containers exist), per-platform enable list, artwork cache
  limit.
- Save policy: RomM per-user (requires link) / Moonbase local.
- Streaming: on/off, proxy the room (default on), TURN server info shown to clients, idle timeout.
- Migration helper: for users who move a filesystem library to RomM, an optional task that matches
  Moonbase saves to RomM ROMs by hash and uploads them.

### 10. Version gating

On connect, read RomM's version (`/api/heartbeat`) and enable features only where the server
supports them:

- Browse, ROMs, firmware, saves/states: all supported 4.x/5.x versions.
- Device registration and 409 conflict tracking: 4.7+. Sync negotiate: 4.9+. The device auth
  flow is newer. Confirm its minimum version before relying on it, and fall back to a pasted client
  token or pair code (`/client-tokens/exchange`) on older servers.
- Streaming: 5.3+ (the per-container config and webstation broker). Earlier experimental streaming
  is ignored.

These thresholds come from RomM release notes and should be confirmed against the changelog.
Report unsupported features in the admin "Test connection" output rather than failing silently.

## Phasing

| Phase | Scope | Result |
|---|---|---|
| 0 | `IGameSource` refactor, no behavior change. `shared/Romm` client + config + test connection. | Groundwork, safe to merge alone. |
| 1 | RomM browse/metadata/artwork + ROM/BIOS proxy with admin token. | Every client except Roku can browse and play RomM games locally. |
| 2 | Per-user linking (device flow), states to RomM, SRAM sync in Core and Smart-TV, conflict UI, play sessions. | Saves round-trip with RomM and other RomM clients. |
| 3 | Play-mode decision + streaming control endpoints + Core stream player (direct room URL, LAN). | PSX/PS2/GC playable on Core TVs without downloading. |
| 4 | Room reverse proxy + remote-key bridge + Smart-TV stream player. | Streaming on webOS/Tizen, with the containers not exposed. |
| 5 | Roku games UI (browse). LL-HLS spike. Multi-disc lazy download. Save migration task. | Rounds out coverage. |

## Risks and open questions

- **WebRTC through a proxy:** signaling proxies fine, media does not. Remote play needs TURN, and the
  plan must document that clearly. Confirm Selkies works with a path prefix behind the plugin.
- **Container scarcity:** one session per container. The UI must handle "busy" gracefully, and
  heartbeats and idle timeouts must release containers promptly.
- **TV WebRTC support:** older webOS/Tizen versions lack WebRTC or hardware decode. A capability probe
  and a clear fallback are required.
- **State portability:** EmulatorJS vs native libretro vs RomM web player states are only
  compatible on the same core and version. Lean on SRAM and label states by core.
- **Emulator naming:** use the exact `emulator` folder names RomM's other sync clients use, so
  saves interoperate. Needs confirming against RomM's sync docs.
- **API drift:** RomM's API moves quickly. Keep all RomM calls in `shared/Romm`, with contract
  tests against recorded responses from at least two RomM versions.
- **Token storage:** per-user RomM tokens are credentials. Encrypt them at rest in plugin data and
  never return them to clients.
