# Sound module — backlog

Ideas for after the module lands. Not required for S2 (the extraction itself).

## "Now playing" integration — announce the current song + artist
Let the pet tell you what's playing on **Spotify** and **YouTube Music** (song title + artist), e.g. a
tray action ("What's playing?") and/or an idle line the pet speaks.

- **Spotify:** the Web API `GET /v1/me/player/currently-playing` (OAuth 2.0 Authorization Code + PKCE,
  scope `user-read-currently-playing` / `user-read-playback-state`). Store the refresh token via the
  host's per-module encrypted settings (`IModuleSettings`, `SettingKind.Secret`). A desktop-only fallback
  is reading the local Spotify client's window title, but the Web API is the clean path.
- **YouTube Music:** no official "now playing" API. Options: the community `ytmusicapi` pattern
  (needs browser auth headers), a local companion (e.g. a browser extension / the `th-ch/youtube-music`
  desktop app which exposes an HTTP "now playing" endpoint), or reading the media session via the
  Windows `GlobalSystemMediaTransportControlsSessionManager` (WinRT) — which actually covers **any**
  media app (Spotify, YT Music, browser) uniformly and needs no per-service OAuth. Prefer the WinRT
  media-session route first; it's the least brittle and most general.
- **Permissions:** this feature makes network calls (Spotify) → the module would then declare
  `ModulePermissions.Network` (and surface that at install/consent time). Sound-only playback stays
  `None`.
- **Speech:** announce via `host.Say` / `host.SayAll` (already `ModulePermissions.Speech` territory).

## Real per-pet identity on AnimationStarted
Today the engine raises `AnimationStarted` with `AnimationInfo.Pet == null` on the sound path: the
sound-selection roll happens in the shared, per-pet-type `Animations` engine, which doesn't know which
specific pet instance triggered the transition (and sound is global, so it doesn't need to). Thread the
real per-pet `IPet` through so `AnimationStarted` carries the actual pet. This is future work that
**S4's AI reactions** will want (e.g. "react when *this* pet starts an emote"). Sound doesn't need it;
it's an ABI enrichment, not a sound feature.
