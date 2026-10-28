# RomM integration plan

Status: proposal. Covers the Moonbase plugin (Jellyfin and Emby), Moonfin Core, Smart-TV and Roku.

## Goal

Let a Moonbase admin point the plugin at a [RomM](https://github.com/rommapp/romm) server so that:

1. **ROMs, BIOS/firmware and all metadata/artwork come from RomM**, not from a folder walk.
2. **Every Jellyfin/Emby user is associated with their own RomM account.** RomM keeps saves, states,
   playtime and personal game data (status, rating, backlog, now playing, last played, hidden) per
   RomM user, so all of it is read and written as that user.
3. **Saves (SRAM), save states, playtime and personal game data sync both ways with RomM**, so they
   line up with the user's other RomM clients (RomM web player, RetroArch/Grout/handheld sync clients).
4. **RomM's server-side emulator streaming** is a first-class play mode. It covers any platform the
   RomM admin has configured a streaming container for. That includes systems too heavy for the client,
   and systems like PSX whose disc images are too big to download to a TV (Smart-TV caps a ROM at
   48 MB, Roku has no local emulator at all). Moonfin does not keep its own list of streamable
   platforms. It reflects whatever RomM's container config offers.

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
| Per-user game data | `RomUserSchema` on every ROM response (`rom_user`: `last_played`, `now_playing`, `backlogged`, `hidden`, `rating`, `difficulty`, `completion`, `status`), written with `PUT /roms/{id}/props` (`roms.user.write`, `update_last_played=true` stamps last played) |
| Play time | `POST /play-sessions` `{device_id, sessions:[{rom_id, save_slot, start_time, end_time, duration_ms}]}`, `GET /play-sessions` (`roms.user.read/write`). Streamed sessions record their own play session and last-played server-side. |
| Streaming | `GET /streaming/config` (which platforms have a container, plus capabilities), `POST /streaming/sessions` (`{rom_id, state_id?, save_id?, memory_card_id?}`, answers 202), `GET /streaming/sessions/{platform}/status` (gives the room `host` URL once launched), `…/heartbeat`, `…/save-state`, `…/load-state`, `…/swap-disc`, `…/save-and-exit`, `DELETE /streaming/sessions/{platform}` |

Streaming itself runs as RomM-managed containers ("webstation" + `romm-broker`). Each container runs
an emulator (SwanStation for PSX, PCSX2, Dolphin, Flycast, any RetroArch core, …) and streams it
to a **browser** over WebRTC via a Selkies web UI. The room URL is that Selkies page. Which
platforms stream is purely RomM configuration: a container can serve any platform its emulator
handles. `GET /streaming/config` returns one row per streamable platform, with its emulator,
label and per-platform capabilities (save slots, live states, memory cards, save picker, import kinds).

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

### 2. Plugin: account association (one RomM account per user)

RomM is multi-user: saves, states, playtime, play sessions and the per-ROM user data all belong
to a RomM user. So **every Jellyfin/Emby user who uses a RomM library is associated with exactly
one RomM account**, and every RomM call made for that user (browse included) uses that account's
token. This is a requirement, not an optional extra. There is no shared-account mode for user data.

- **Association record.** The plugin stores `{jellyfinUserId → rommUserId, rommUsername, clientToken
  (encrypted), linkedAt, scopes}` in plugin data. It is one-to-one: a RomM account can't be linked to
  two Jellyfin users. Otherwise their saves and playtime would merge.
- **Linking (user-initiated):** a "Link RomM account" action in each client's settings, and a prompt
  the first time an unlinked user opens a RomM library. The plugin starts RomM's device
  authorization flow on the user's behalf. The client shows the `user_code` and a QR code
  (TV-friendly, no typing), and the user signs in and approves on their phone. The plugin polls
  `/auth/device/token` and stores the client token. On servers without the device flow, the user
  pastes a RomM pair code instead (`/client-tokens/exchange`).
- **Linking (admin-assisted):** on the plugin config page, the admin can map Jellyfin users to RomM
  users (pick from `GET /users` with an admin token) and send each a pairing request. Optional
  auto-match by identical username or email, which the admin confirms. The user still approves the
  token, so the plugin never holds a RomM password.
- **Automatic association** when both servers share an identity provider (OIDC or LDAP): see 2a.
- **Unlinked users:** they see a "Link your RomM account to play" card instead of the library.
  The admin token is used only for server-level work (test connection, listing RomM users for
  mapping, artwork cache warmup) and never to read or write user data.
- **Devices:** each Moonfin client device a user plays on is registered as a RomM device under that
  user (`POST /devices`, name like "Moonfin – Living room webOS"). Its id is sent with saves and play
  sessions, so RomM's conflict tracking and device list work. The user can see and revoke these in RomM.
- **Unlink / revoke:** unlinking deletes the stored token, and RomM-side revocation surfaces as `401`.
  On `401` the plugin marks the association as needing a re-link, and clients show the link prompt
  again instead of failing. Local Moonbase data for that user is not touched.
- Scopes requested: `roms.read platforms.read firmware.read assets.read assets.write
  devices.read devices.write roms.user.read roms.user.write me.read`.
- Visibility comes for free: because calls are made as the user, RomM enforces hidden ROMs and
  platforms, and the `streaming` permission grant, itself.

### 2a. Automatic association through a shared identity provider

When Jellyfin/Emby and RomM authenticate against the same directory or IdP, the plugin should
connect a user to their RomM account without the user doing anything, or at most one tap. That
splits into two problems: **matching** the identities, and **getting a RomM token** for the
matched account.

**What each side supports today**

| Server | OIDC | LDAP |
|---|---|---|
| RomM | Built in (`OIDC_*` env). Users carry `oidc_issuer` + `oidc_sub` (unique index), the username comes from `OIDC_USERNAME_ATTRIBUTE` (default `preferred_username`), and email is IdP-managed (`email_managed_by_oidc`). `/api/heartbeat` reports `OIDC.ENABLED` and `PROVIDER`. | **None.** A directory reaches RomM only through an OIDC IdP that federates it (Authentik, Keycloak, Authelia+LLDAP, Zitadel, …). |
| Jellyfin | SSO-Auth plugin (OIDC), which keeps a per-provider link from IdP subject to Jellyfin user | LDAP-Auth plugin. The user's auth provider is the LDAP provider and the username is the configured LDAP uid attribute. |
| Emby | No OIDC | LDAP plugin, if installed |

So the supported shapes are:

| Shape | Match key (strongest first) |
|---|---|
| OIDC on both, same IdP | `(issuer, sub)` exact. Falls back to verified email, then `preferred_username`. |
| LDAP on Jellyfin/Emby, OIDC on RomM through an IdP backed by the same directory | The directory attribute both sides expose: email (`mail` ⇄ `email` claim) or username (`uid`/`sAMAccountName` ⇄ `preferred_username`). The admin picks which. |
| Anything else | Manual or admin-assisted linking (section 2) |

**Matching rules (plugin)**

- The admin turns on "Auto-link RomM accounts" and chooses the match keys allowed. Matching runs on
  a user's first RomM request, on login, and in a scheduled task that pre-links everyone.
- Jellyfin identity: the SSO-Auth link for `(issuer, sub)`, the LDAP username, and the user's
  email. Each is read only if that auth plugin owns the user, so a local Jellyfin account never
  auto-links by name. The SSO-Auth and LDAP-Auth data is read through their stored configuration.
  This is another plugin's internal format, so it sits behind an adapter with tests, and auto-link
  is disabled if the format isn't recognised.
- RomM identity: `GET /api/users` with the admin token gives username and email. `(issuer, sub)` is
  stored but not returned by `UserSchema` today, so exact-subject matching needs the small upstream
  addition below. Until then the match is on email or username.
- Safety:
  - Only link to RomM users that are IdP-managed (`email_managed_by_oidc` true, or
    `has_password` false). A local RomM account that happens to share a name is never matched.
  - Exactly one candidate, or no link. Ambiguous cases go to the admin's review list.
  - The one-to-one rule from section 2 still holds.
  - Every auto-link is recorded (who, which key, when) and shown in the Accounts table, where the
    admin can undo it.
- Provisioning: if the user has no RomM account yet and RomM has `OIDC_ALLOW_REGISTRATION`
  on, the account is created the first time they pass through RomM's OIDC login (path B below).
  Otherwise they stay unlinked with a "No RomM account" status.

**Getting a token for the matched account**

RomM currently has no way for a third party to obtain a token for another user. Client tokens
are minted only by their owner, `client_credentials` is an unimplemented TODO in `/api/token`, and
there's no token-exchange grant. So the plan has a path that works today and a fully automatic
path that needs a small upstream RomM feature:

- **B. One-tap, works today.** Matching is automatic, and only the approval needs the user. The
  plugin starts the device flow already targeted at the matched account. Where the client has a
  browser surface (Core on desktop/mobile/Android TV, the web app), it opens RomM's approval page
  in a WebView. With `OIDC_AUTOLOGIN` on and an existing IdP session, that is sign-in-free, and the
  user taps "Approve". On TVs the QR code goes to the phone, where the IdP session usually already
  exists. Either way there's no password and no account choice. The plugin checks the approved
  token's `/api/users/me` against the expected match and rejects a token for the wrong account.
- **A. Fully automatic, needs RomM upstream (recommended proposal).** Delegated token issuance for a
  trusted service:
  - A new admin-grantable scope (e.g. `users.tokens.delegate`) on a client token the admin
    creates for Moonbase.
  - `POST /api/client-tokens/delegate {user: {id | oidc_issuer+oidc_sub | username}, scopes,
    device_name}` returns a client token owned by that user. Scopes are capped to the
    delegator's own and to a non-admin allow-list, and the call is refused for admin users.
  - The token appears in the user's own token list labelled "issued by Moonbase", and they can
    revoke it. Each issuance writes an audit event (RomM already audits token creation).
  - `UserSchema` exposes `oidc_issuer`/`oidc_sub` to `users.read` callers, for exact matching.

  This works for every shape above, LDAP included, because trust comes from the admin who
  configured both servers rather than from a user-presented token. It fits RomM's existing
  `ClientToken` model and is roughly an endpoint plus a scope plus tests. The plugin detects it
  through RomM's version and OpenAPI, and uses A when present, otherwise B.
- **C. Considered, not chosen: RFC 8693 token exchange** with the user's IdP token. This is the
  cleanest trust model, but Moonbase never holds the user's IdP token. Jellyfin's SSO plugin
  doesn't share it, and LDAP logins have none. Getting one would mean IdP-side impersonation,
  which only some IdPs support. Worth revisiting if RomM adds token exchange for its own reasons.

**Lifecycle**

- Re-validate on each Jellyfin login and in the scheduled task:
  - The RomM user is disabled or deleted, or the IdP subject changed: the association moves to
    "needs re-link".
  - The Jellyfin user is deleted or disabled: the plugin revokes its RomM token (`DELETE
    /client-tokens/{id}` as the user) and drops the association.
- Group/role mapping stays each server's own business. RomM's `OIDC_ROLE_*` decides RomM
  permissions, including the `streaming` grant, and Moonbase never escalates them.

### 3. Plugin: browse, metadata, artwork

- Systems: map RomM `platform.slug` to Moonfin system ids and local cores through a new table next
  to `GameSystemCoreResolver` (e.g. `psx → psx/pcsx_rearmed`, `snes → snes`). That table only
  answers "is there a local core". Every RomM platform the user can see is listed, whether or not it
  has a local core, and whether it can stream comes from `/streaming/config` (see 7). A platform
  with neither is listed but marked "not playable here".
- Games: map `RomSchema` to `GameSummary`/`GameDetail` (name, summary, genres, companies,
  franchises, regions, release year, players, rating, alternative names, revision, size). RomM
  already merges IGDB, ScreenScraper, MobyGames, LaunchBox and other sources, so **RDB and LaunchBox
  lookups are skipped for RomM games**.
- Artwork: serve RomM's cover and screenshots through the existing artwork-manifest protocol
  (protocol 2: versioned URLs, revision = RomM `updated_at` hash) and cache them in
  `GameArtworkStore`, so TVs never hit RomM directly. Extend `GameDetail` with optional fields
  clients can adopt over time: `screenshots[]`, `manualUrl`, `videoId`, `achievementsId` (`ra_id`),
  `similar[]`.
- Per-user game data: map `rom_user` onto `GameDetail.userData` (`lastPlayed`, `nowPlaying`,
  `backlogged`, `hidden`, `rating`, `difficulty`, `completion`, `status`, plus total playtime from
  `/play-sessions`). Clients get "Continue playing" (by `last_played`), "Backlog" and "Favorites"
  rows and status/rating controls on the details screen. Edits go through
  `PUT /Moonfin/Games/{lib}/Games/{id}/UserData`, which writes `PUT /roms/{id}/props` as the user.
  RomM is the source of truth, and Moonbase stores none of this for RomM games. Moonfin's existing
  personal rating maps to RomM `rating`.
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
- **Play time and last played:** local play records a session in the client (start, end and
  active duration, with paused/backgrounded time excluded). The plugin ingests it to RomM with
  `POST /play-sessions` (with `device_id`) and stamps `PUT /roms/{id}/props?update_last_played=true`.
  It also sets `now_playing`, if the user's setting allows. Sessions are queued on the device and
  retried, so offline play still counts once the device reconnects. Streamed sessions are already
  recorded server-side by RomM's streaming lifecycle, so the plugin **must not** post a duplicate for
  them.

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
2. Use local if a local core exists on this device for the platform **and** `downloadBytes` fits the
   device budget (Smart-TV: the current 48 MB blob ceiling. Core: free cache space with headroom,
   plus a per-device "maximum download size").
3. Otherwise stream, if `stream.available`. That is true for any platform RomM has a container
   for, including platforms Moonfin has no local core for at all (PS2, GameCube, Wii, Wii U, PS3,
   3DS, …).
4. Otherwise show "Can't play on this device" with the reason.

User settings: **Play mode = Auto / Prefer local / Prefer streaming**, plus a per-device
"maximum download size". With this, PSX on a TV streams by default, while a desktop or phone with
space can still download and play it natively.

### 7. Server-side emulation (streaming)

**Platform coverage is data-driven.** The plugin keeps no list of streamable platforms or
emulators. It reads `/streaming/config` as the linked user (so RomM's `streaming` permission and
platform visibility apply), caches it briefly, and marks `playModes.stream` on every game whose
`platform_slug` has a row. Labels ("SwanStation", "RA PPSSPP") and capabilities come from the
same rows. When the RomM admin adds a container for a new platform, it shows up in Moonfin with no
plugin or client release. Clients render the in-game menu from capabilities only: save/load
slots if `max_slots`/`supports_live_states`, disc swap if supported, memory-card picker if
`supports_memory_cards`. Nothing is keyed off a platform name.

**Plugin endpoints** (thin, authenticated wrappers around RomM's streaming API, called as the
linked user):

- `GET /Moonfin/Games/Streaming/Config` → the streamable platforms and their capabilities, as RomM reports them.
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

1. Claim → status poll with a "Starting <emulator label>…" screen showing RomM's launch phases (large
   titles unpack first).
2. Open the room in a WebView (Core: reuse the EmulatorJS WebView host, Android TV/desktop/iOS
   WebViews all have WebRTC) or a full-screen iframe (Smart-TV: webOS and Tizen builds with WebRTC.
   Gate by a capability check and fall back to "not supported on this TV").
3. Heartbeat every ~15–30 s while visible. Pause/background → `save-and-exit` after a grace period,
   because containers are scarce. Exit → `save-and-exit` (`wait: true`), then return to details.
4. The resume picker uses RomM states for the container's emulator namespace (from `/streaming/states/{rom_id}`).
   The in-game menu follows the platform's capabilities. For example, PSX via SwanStation has no
   live slots, only the exit autosave, so the menu shows just "Save & quit".

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

- RomM tab: URL, admin token (server-level tasks only), "Test connection" (shows RomM version,
  platforms, whether streaming is enabled and the platforms its containers serve, read live from
  RomM), per-platform enable list, artwork cache limit.
- Accounts: a table of Jellyfin/Emby users with their linked RomM account, link status (linked,
  auto-linked, pending approval, ambiguous, no RomM account, needs re-link), how the match was made,
  and last sync. Actions: map to a RomM user, send pairing request, unlink.
- Identity: auto-link on/off, which match keys are allowed (OIDC subject / email / username), the
  detected setup (e.g. "Jellyfin: SSO-Auth (OIDC) · RomM: OIDC via Authentik"), whether RomM
  supports delegated tokens (fully automatic) or one-tap approval is used, and a dry-run button
  that previews the matches before enabling.
- Streaming: on/off, proxy the room (default on), TURN server info shown to clients, idle timeout.
  There's no per-platform streaming list here. That lives in RomM's container config.
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
| 1 | Account association (device flow + admin mapping + auto-matching with one-tap approval), device registration, then RomM browse/metadata/artwork + ROM/BIOS proxy as the linked user, per-user game data (status, rating, last played, rows). | Every client except Roku can browse and play RomM games locally, each user seeing their own RomM data. |
| 2 | States to RomM, SRAM sync in Core and Smart-TV, conflict UI, playtime ingest with offline queue. | Saves and playtime round-trip with RomM and other RomM clients. |
| 3 | Play-mode decision + config-driven streaming endpoints + Core stream player (direct room URL, LAN). | Any platform RomM has a container for is playable on Core devices without downloading. |
| 4 | Room reverse proxy + remote-key bridge + Smart-TV stream player. | Streaming on webOS/Tizen, with the containers not exposed. |
| 5 | Fully automatic linking via the upstream RomM delegated-token feature, once merged (propose the PR alongside phase 1). Roku games UI (browse). LL-HLS spike. Multi-disc lazy download. Save migration task. | Rounds out coverage. |

## Risks and open questions

- **Upstream dependency for zero-touch linking:** fully automatic linking needs the RomM
  delegated-token feature (2a, path A). Without it the best is one tap. Open the discussion with
  RomM maintainers early.
- **Reading other plugins' data:** the SSO-Auth and LDAP-Auth link and username data aren't a public
  API. Keep that behind an adapter that fails closed to manual linking.

- **Association mismatches:** two Jellyfin users wanting one RomM account (e.g. a shared family
  login) would merge saves and playtime. The one-to-one rule blocks this. The admin UI should
  explain why and suggest separate RomM users.

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
