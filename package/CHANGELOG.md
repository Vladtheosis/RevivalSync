*Made by Revival*

**Source code / report bugs / help develop:** https://github.com/Vladtheosis/RevivalSync

> **HIGHLY EXPERIMENTAL — testing phase. Prone to game-breaking bugs. Not for normal play.**

# 1.0.5

- Pushing doors open with a cart (or your body, or held loot) no longer fights the sync:
  while you're physically pushing a door, it goes fully local so it swings immediately,
  then settles back to the host's state after — no more delayed/buggy door shoving
- Held objects no longer trust stale velocity data when the host pauses sending
  (a subtle source of weird cart corrections)

# 1.0.4

- Interim re-release published directly on Thunderstore: same code as 1.0.3, packaging tweaks only

# 1.0.3

- Fixed choppy shadowed objects: Photon only sends packets when something changes, and the
  blend treated stale data as live — objects vibrated/drifted around old targets. Silence now
  correctly means "the host's copy is at rest exactly there", and while packets flow the blend
  leads the target by the data's age for continuous motion. Blends also use interpolation-aware
  physics moves instead of raw position writes (no more visible stepping)
- Fixed cart desync when dragging from the body ("weak" mode): the local steering drive now
  engages only for handle grabs, matching what the host actually simulates
- Doors and cabinets are simulated locally again — done right this time (the way the original
  NetworkingReworked did it): the game's own hinge logic (closing torque, latching, bounce,
  stabilization) runs on your machine, the local joint is kept and unbreakable, host decides
  breaks and we mirror them. Doors respond instantly (config: SimulateHinges)

# 1.0.2

- CRITICAL: fixed the "no internet" bug (couldn't host or join, no error shown) that came back
  in 1.0.0/1.0.1. The new host-state capture hooked a PhotonNetwork method during mod loading,
  which force-starts Photon too early and silently kills all connectivity. The hook is now
  applied only after the game starts Photon itself. **Update from 1.0.1 immediately.**
- CRITICAL: discovered that the sync system's update loops (passive blending, drift
  correction, cart steering assist, handbacks — the entire anti-desync machinery) never
  actually ran in ANY previous version: this game never runs Update on mod-created objects.
  The loops now ride verified in-game hooks. This is very likely the root cause of most
  desync reported so far — this version is effectively the architecture's first real run.

# 1.0.1

- Marked the mod clearly as experimental/testing-phase in all package files
- VerboseLogging is now ON by default and records everything the sync system does:
  object registrations, client-mode transitions, grabs/releases/throws, snaps (with reason
  and distance), handbacks, dedupes, stale-host-data warnings, and 10-second stats
  (registered objects, held objects, host packet flow)

# 1.0.0

- Merged NetworkingRevived and NetworkTweaksRevived into one mod
- Host state is now captured at the raw network-event level (the original NetworkingReworked's
  proven technique) — immune to component order, other mods' patches, and the silent capture
  failures that left carts without host data
- Fixed held objects being skipped by drift correction when the game force-flags them
  "kinematic" for clients
- Doors and cabinets reverted to host-driven (with smoothing): their springs, joints and
  breaking are host-managed game logic that cannot be replicated locally without the
  springy/broken-door bugs — this matches what the original NetworkingReworked did
- Guaranteed cart steering, cargo riding, stuck-object snapping, velocity-aware drift
  correction, post-throw grace, timeout fix, LateUpdate dispatch and SmoothSync all carried
  over from the previous mods
- VerboseLogging now prints per-second held-object diagnostics (speed, drift, host packet age)
