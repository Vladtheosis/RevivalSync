# RevivalSync

Made by **Revival**

**Source code — help welcome:** https://github.com/Vladtheosis/RevivalSync
(issues, bug reports with logs, and pull requests are all appreciated; see `DEVLOG.md` there
for the architecture and lessons learned)

One mod that makes R.E.P.O. multiplayer feel like singleplayer — **the replacement for
NetworkingReworked**, which no longer works on current game versions. Also replaces (and
merges) NetworkingRevived and NetworkTweaksRevived. Rebuilt on NetworkingReworked's
architecture, including its host-state capture technique.

> **Still experimental.** It should work fine for normal play, but this mod predicts
> physics locally, so odd behaviour can still show up — an item in the wrong spot, a door
> disagreeing with the host, loot behaving strangely in a cart. Nothing that breaks a run
> permanently, and every feature can be switched off individually (see Config). Bug
> reports with a log attached are genuinely useful.

**Credits:** several core techniques are adopted from **readthisifbad's NetworkingReworked**
— the raw host-state capture, the fully-local door model with event-level reconciliation,
and the release-state overwrite that makes throws fly clean. Thank you.

## What it does (client-side only — works with unmodded hosts)

- **Instant grabs**: valuables, props, items and carts respond the moment you grab them,
  no host round trip. Carts follow you at full speed with the game's real steering feel;
  weapons aim with your camera using their own orientation tuning; throws fly the way you
  threw them. Only self-moving objects (vehicles, drones, the duck) use the game's normal
  sync.
- **Instant doors**: doors and cabinets run the game's own hinge logic locally, so they
  swing the moment you touch them (the host still decides when they break).
- **Synced world**: everything you're not holding glides smoothly toward the host's
  authoritative state, so desync cannot accumulate. Wedged/stuck objects snap free.
- **Emergency resync (F8)**: if something ever ends up in the wrong place, press F8 to
  teleport every synced object to exactly where the host has it. The key is configurable,
  and "Auto Resync Seconds" can do it for you on a timer.
- **Smooth everything else**: enemies and other host-driven objects move with snapshot
  interpolation instead of choppy stepping.
- **No random timeout kicks**: Photon's client-side disconnect timer is neutralized via
  Photon's own settings (zero patches).
- **Faster networking**: packets processed every rendered frame.

Item *effects* — damage, explosions, battery drain, breaking — stay host-decided exactly
like vanilla; only the physics feel is local.

## Known issues

- **Doors and cupboards can occasionally appear desynced** from what the host sees
  (e.g. open on one screen, closed on the other). This is actively being worked on —
  if it happens to you, a report with `Verbose Logging` on and your `LogOutput.log`
  attached helps a lot.

Both players can (and should) install it — it automatically does nothing while you're the
host and activates when you're the client.

## Config

**If anything misbehaves for you, you can turn that part of the mod off** — every feature
has its own switch in section "1. Main". Easiest with the in-game settings menu from the
**REPOConfig** mod; otherwise edit `BepInEx/config/com.Revival.revivalsync.cfg`.
Three sections:

- **1. Main** — plain on/off switches (instant carts / doors / items, no timeout kicks,
  smooth enemies). Safe to flip; everything defaults to ON.
- **2. Fine-Tuning** — numbers for how firmly things sync. The defaults are good; touch
  only if you're troubleshooting something specific.
- **3. Debug** — `Verbose Logging`: turn ON before reporting a bug and include your
  `BepInEx/LogOutput.log` with the report.

## Install

1. Install BepInEx (BepInExPack for R.E.P.O.).
2. Drop `RevivalSync.dll` into `BepInEx/plugins`.
3. Remove NetworkingRevived, NetworkTweaksRevived, NetworkingReworked and REPONetworkTweaks
   if you still have them — RevivalSync replaces all of them and will warn if they're present.
