# RevivalSync

Made by **Revival**

**Source code — help welcome:** https://github.com/Vladtheosis/RevivalSync
(issues, bug reports with logs, and pull requests are all appreciated; see `DEVLOG.md` there
for the architecture and lessons learned)

One mod that makes R.E.P.O. multiplayer feel like singleplayer — the successor to (and
merger of) NetworkingRevived and NetworkTweaksRevived, rebuilt on the architecture of the
original NetworkingReworked with its host-state capture technique.

**Credits:** several core techniques are adopted from **readthisifbad's NetworkingReworked**
— the raw host-state capture, the fully-local door model with event-level reconciliation,
and the release-state overwrite that makes throws fly clean. Thank you.

## What it does (client-side only — works with unmodded hosts)

- **Instant grabs**: valuables, props, shop items and carts respond the moment you grab
  them, no host round trip. Carts follow you at full speed with the game's real steering
  feel; weapons straighten out in your hand; throws fly the way you threw them.
- **Instant doors**: doors and cabinets run the game's own hinge logic locally, so they
  swing the moment you touch them (the host still decides when they break).
- **Synced world**: everything you're not holding glides smoothly toward the host's
  authoritative state, so desync cannot accumulate. Wedged/stuck objects snap free.
- **Smooth everything else**: enemies and other host-driven objects move with snapshot
  interpolation instead of choppy stepping.
- **No random timeout kicks**: Photon's client-side disconnect timer is neutralized via
  Photon's own settings (zero patches).
- **Faster networking**: packets processed every rendered frame.

Item *effects* — damage, explosions, battery drain, breaking — stay host-decided exactly
like vanilla; only the physics feel is local.

Both players can (and should) install it — it automatically does nothing while you're the
host and activates when you're the client.

## Config

`BepInEx/config/com.Revival.revivalsync.cfg` — simulation toggles (carts, doors, items),
sync strengths, SmoothSync tuning, timeout toggle. When reporting a bug, set
`VerboseLogging = true` first — it records everything the sync system does
(registrations, grabs, snaps, handbacks, host packet flow) — and include your
`BepInEx/LogOutput.log`.

## Install

1. Install BepInEx (BepInExPack for R.E.P.O.).
2. Drop `RevivalSync.dll` into `BepInEx/plugins`.
3. Remove NetworkingRevived, NetworkTweaksRevived, NetworkingReworked and REPONetworkTweaks
   if you still have them — RevivalSync replaces all of them and will warn if they're present.
