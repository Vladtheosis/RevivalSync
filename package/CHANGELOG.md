*Made by Revival*

**Source code / report bugs / help develop:** https://github.com/Vladtheosis/RevivalSync

# 1.1.2

Three fixes powered by studying the original NetworkingReworked's code (credit:
readthisifbad — techniques adopted with thanks):

- Thrown objects no longer stop mid-air when the throw grace ends: on release, the cached
  host state is overwritten with our own release state (NetworkingReworked's trick), so
  corrections never reference the host's stale "still in your hand" data
- Opening doors/cabinets is no longer delayed: a door in local motion now belongs entirely
  to local physics (NetworkingReworked's door model) — continuous rotation sync was
  fighting the locally-running hinge logic. The host is followed only when *its* door moves
  while yours rests (another player using it), plus a gentle long-idle reconcile
- Held tools' residual jitter fixed: the rotation mirror now steers angular velocity
  instead of forcing the transform each tick, so it cooperates with the grab physics
  instead of fighting it

# 1.1.1

- Fixed held weapons vibrating violently (1.1.0 regression): running the game's weapon
  orientation code locally fights the client-side grab physics. Weapons now mirror the
  host's straightened rotation instead, like every other gadget — straight in hand, no fight
- Fixed thrown objects "moving weirdly": right after a throw the host still thinks the
  object is in your hand, and corrections dragged the flight toward that stale data.
  Throws now fly on pure local physics for the grace period (config: PostThrowGrace),
  then reconcile smoothly

# 1.1.0 — first stable release

- Held weapons now straighten out in your hand like they do for the host: guns and melee
  weapons run the game's own orientation logic locally (instant), and every other gadget
  mirrors the host's straightened rotation while held
- Objects that drifted from the host's state no longer jiggle their way back or trail far
  behind: position forcing is gone entirely for physics objects, replaced by
  distance-scaled velocity steering that closes big gaps in under half a second — smoothly
- Out of the experimental phase: warnings retired, VerboseLogging defaults to off
  (turn it on and attach LogOutput.log when reporting bugs)

# 1.0.7

- Shop items (weapons, swords, grenades, energy crystals, tools...) are now simulated
  locally like valuables and carts: they follow your hand instantly instead of being
  host-driven with interpolation delay stacked on top ("tools feel laggy"). Thrown
  items fly with your predicted throw too. Their effects — damage, explosions, battery
  drain, breaking — remain host-decided exactly like vanilla.
  Config: Simulation > SimulateItems

# 1.0.6

- Fixed world-wide vibration/jitter ("phone on max"): sync corrections were forcing object
  positions every physics tick, fighting the physics engine 50x a second while chasing a
  target that resets with every network packet. Corrections now steer velocity instead —
  objects glide to the host's state and rigidbody interpolation stays smooth. Position
  forcing only remains for large divergences (guaranteed convergence) and host-kinematic
  objects
- Held-object drift corrections are now smooth velocity nudges too (no position stepping)
- Objects that are already in sync get a small deadband: no more micro-shimmer from
  chasing per-packet noise while at rest
- The network lag estimate is clamped so Photon clock noise can't kick host positions around
- Note: if you tuned SmoothSync RateSmoothing up, set it back to 0.1 — at 1.0 the update-rate
  estimate follows every packet-timing wobble and makes enemies/players jitter

# 1.0.5

- Pushing doors open with a cart (or your body, or held loot) no longer fights the sync:
  while you're physically pushing a door, it goes fully local so it swings immediately,
  then settles back to the host's state after — no more delayed/buggy door shoving
- Held objects no longer trust stale velocity data when the host pauses sending
  (a subtle source of weird cart corrections)
- When the host stops sending entirely (game hang, host quitting, connection loss), the sync
  no longer pins everything to the host's frozen last-known state — carts, doors and loot
  stay fully usable locally until data flows again (the "everything was stuck" freeze)

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
