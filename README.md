# RevivalSync

Made by **Revival**

> ## ⚠️ HIGHLY EXPERIMENTAL — NOT FOR NORMAL PLAY
> This mod is in an active testing phase and is **prone to many game-breaking bugs**:
> desynced objects, broken carts and doors, loot behaving differently for each player,
> and anything else physics-related going wrong. **Do not use it for runs you care
> about.** Install it only if you're helping test, and expect things to break.

One mod that makes R.E.P.O. multiplayer feel like singleplayer — the successor to (and
merger of) NetworkingRevived and NetworkTweaksRevived, rebuilt on the architecture of the
original NetworkingReworked with its host-state capture technique.

## What it does (client-side only — works with unmodded hosts)

- **Instant grabs**: valuables, props and carts respond the moment you grab them, no host
  round trip. Carts follow you at full speed with the game's real steering feel.
- **Synced world**: everything you're not holding is continuously blended toward the
  host's authoritative state, so desync cannot accumulate. Wedged/stuck objects snap free.
- **Smooth everything else**: enemies, doors, and other host-driven objects move with
  snapshot interpolation instead of choppy stepping.
- **No random timeout kicks**: Photon's client-side disconnect timer is neutralized via
  Photon's own settings (zero patches).
- **Faster networking**: packets processed every rendered frame.

Doors and cabinets are deliberately host-driven (like the original NetworkingReworked):
their joints and springs are managed by host-only game code and cannot be simulated
locally without breaking them. They're smoothed, but open on the host's authority.

Both players can (and should) install it — it automatically does nothing while you're the
host and activates when you're the client.

## Config

`BepInEx/config/com.Revival.revivalsync.cfg` — simulation strengths, cart options,
SmoothSync tuning, timeout toggle. `VerboseLogging` (ON by default during the testing
phase) records everything the sync system does — registrations, grabs, snaps, handbacks,
host packet flow, stale-data warnings — so bug reports come with the full story attached.
Include your `BepInEx/LogOutput.log` when reporting problems.

## Install

1. Install BepInEx (BepInExPack for R.E.P.O.).
2. Drop `RevivalSync.dll` into `BepInEx/plugins`.
3. Remove NetworkingRevived, NetworkTweaksRevived, NetworkingReworked and REPONetworkTweaks
   if you still have them — RevivalSync replaces all of them and will warn if they're present.
