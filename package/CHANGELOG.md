*Made by Revival*

**Source code / report bugs / help develop:** https://github.com/Vladtheosis/RevivalSync

# 1.2.15

- Fixed carts syncing position but staying rotated 90 degrees off: the game's own cart
  logic (stabilization etc.) was running locally for carts nobody here was holding and
  fought the rotation sync. It now only runs for a cart YOU hold. The stuck-loot
  teleport backstop also counts rotation now, so a wrongly-angled cart gets rescued
  like a wrongly-placed one
- Fixed loot sliding off carts during stop-start pushing: cargo no longer flips between
  riding-free and synced every time the cart pauses (each flip jostled the load) — it
  stays riding until the cart has been calm for a moment
- Loot the host adds to a not-yet-synced cart should no longer press against the outside
  of the basket: that came from the cart's wrong rotation putting the host's cargo spots
  outside your basket, fixed above

# 1.2.14

- Held objects now follow NetworkingReworked's rule too: while you're holding something,
  the network doesn't touch it at all. The drift correction (which fought the normal
  ping-trail and acted as a brake — the old "ultra slow cart") and the wedge-snap (which
  mistook a paused cart's trail for a stuck object and teleported it out of your hands)
  are both gone. One safety remains: if the host's copy is hopelessly far away for over a
  second it's snagged on something, so control returns to the host
- Removed the now-meaningless "Held Object Correct At" setting

# 1.2.13

- Loot syncing rebuilt on the original NetworkingReworked's code (credit: readthisifbad).
  Objects you aren't holding are simply eased toward the host's last known state every
  physics tick — position, rotation and both velocities — and that's the entire algorithm,
  the same one the old mod used. Everything layered on top of it over 1.2.x (predicting
  the target by ping, idle detection, deadbands, velocity steering, wedge timers) each
  fixed one symptom and caused the next, because they all made the sync target move
  between packets. A still target converges smoothly and can't fight the physics
- The host's position is also taken exactly as sent again, instead of being projected
  forward by network lag — that projection was behind the vibrating and jittering reports
- Kept on top of it: the stuck-loot teleport backstop, the resync key, throw grace, cart
  cargo riding free, and the pooled-object guard

# 1.2.12

**Still experimental:** this mod predicts physics on your machine, so occasional oddities
(an item in the wrong spot, a door disagreeing with the host, strange loot behaviour) can
still happen. It should play fine otherwise, nothing breaks a run permanently, and every
feature can be switched off individually in the config.

- Cart cargo rebuilt on the original NetworkingReworked's model (credit: readthisifbad):
  while a cart is being used, the loot inside it now gets NO network correction at all —
  it just rides in the basket on local physics, the way it did in the mod that worked.
  Every version that tried to correct loot mid-haul failed in its own way (rattling,
  sliding out, fighting the basket). When the haul ends, the load settles back into sync
  item by item, staggered, so nothing jerks
- Fixed valuables teleporting around the level: the game parks deactivated objects far
  above the map, and the mod was syncing toward that parking spot — flinging objects
  into the sky and snapping them back. One session did this 272 times to the same
  scenery. Pooled objects are now left alone

# 1.2.11

- Fixed slippery cart loot for real: cargo was being pulled toward where the host has it
  in the WORLD, but the host's copy of a moving cart trails yours by your ping — so every
  tick the loot got shoved backwards out of the basket. Cargo is now corrected relative
  to its own cart, so the lag cancels out and items simply sit where the host has them
  inside the basket. Anything genuinely out of place is returned to its spot
- Stopped thousands of pointless teleport-snaps: some objects (dead players' heads above
  all) get flagged as teleporting by the host every few frames even when nothing moved —
  one session logged 3710 of them for a single head. Only real teleports act now

# 1.2.10

- Fixed loot lying beside the cart while the host sees it inside: cart cargo now gets a
  gentle keep-in-the-basket pull (strong enough to stay aboard, soft enough not to
  rattle), and the stray-snap tightened to 2m
- Fixed objects resting in the wrong place forever: an object that fell asleep away from
  the host's position was immune to every correction — sleeping objects now wake up
  whenever they slept more than half a meter from where the host has them, then glide home
- Fixed laggy drone carrying: drones are now simulated like normal items while you hold
  or carry them (instant hand feel) and only hand over to the host while actually flying
  (toggled on) or magnet-carrying something

# 1.2.9

- Found the standing desync: loot riding in carts ran pure local physics with NO safety
  net, and in a busy lobby carts are always "in use" — cargo drifted unboundedly and
  nothing ever corrected it (this is why the 5-second teleport never fired). Riders now
  snap back whenever they stray more than 3m from the host's position
- Fixed the feather drone properly: the game's override system (mass/drag/torque) turned
  out to be host-only INSIDE the method that applies it — every override the mod set on
  clients was silently ignored. The original NetworkingReworked patched this exact
  method; now we do too. Feather-carried loot finally feels light
- New: Auto Resync Seconds (default 0 = off) — set it and the mod presses the resync
  key for you automatically at that interval, no more F8 spam
- Note: the drone itself flying with a slight delay is by design (it pilots itself, so
  the host owns it, like enemies)

# 1.2.8

- Fixed the drone handoff for real: 1.2.7's version fought the player's own grab (the
  feather drone exists to HELP you carry — you hold the piano while the drone lifts it)
  and looped register/unregister hundreds of times, which was the "insane desync".
  New rule: your grab always wins; while you carry a feather-lifted object the mod
  applies the drone's lightweight physics locally so it feels light for you too; only
  objects held by the drone ALONE go host-driven
- New: Resync Loot Key (default F8, configurable) — press to instantly teleport every
  synced object to exactly where the host sees it. The emergency desync fix, inspired
  by the original NetworkingReworked's hard sync (credit: readthisifbad)
- New: convergence backstop — any object that stays more than 1.5m from the host's
  position for over 5 seconds teleports there, guaranteed. Loot always ends up where
  the host sees it

# 1.2.7

- Fixed magnet drones (Feather etc.) making objects immovable: the drone applies its
  physics (half mass, zero gravity, drag) on the host only, so your locally-simulated
  copy stayed at full weight fighting a floating host copy. Following the mod's
  ownership rule — the host owns whatever provides its own motion — a drone-grabbed
  object now hands itself back to vanilla sync until the drone releases it
- Note: if the whole game feels laggy/desynced, check that the switches in config
  section "1. Main" are ON — they are the mod. All four had been switched off in
  testing, which plays exactly like vanilla (laggy doors and all)

# 1.2.6

- Held carts can no longer vanish from your hands: the anti-stuck teleport mistook a
  busy lobby's normal multi-meter trail (cart paused, host copy catching up) for a
  wedged object and "freed" it to the host position mid-grab. Carts are now fully
  exempt from that teleport, other held items need much stronger evidence, and the
  cart handback tolerance scales up for high-traffic lobbies
- Session logs are now archived automatically to BepInEx/RevivalSync-logs (newest 10
  sessions, refreshed every minute during play) — bug evidence survives restarts and
  crashes for troubleshooting
- Reminder: if any feature misbehaves for you, every part of the mod can be switched
  off individually in section "1. Main" of the config — in-game via the REPOConfig
  mod's menu, or in BepInEx/config/com.Revival.revivalsync.cfg

# 1.2.5

- Interim release, number used outside this changelog

# 1.2.4

- Loot in carts no longer rattles: cargo now rides in pure local physics whenever the
  cart is in use by ANY player or still rolling (previously only your own cart counted) —
  the original NetworkingReworked's cargo rule, adopted with thanks
- Door jitter fixed for real this time: host authority over door angles is now applied
  through angular velocity (joint-friendly) instead of rotation forcing, which fought
  the hinge joint and the door's own spring 50 times a second
- Objects should no longer fall through floors on clients: simulated objects get
  continuous collision detection, so fast corrections and hard throws can't tunnel
  through thin geometry
- Known issue still under investigation: objects passing through other players and loot
  misbehaving near them — client-side logs from an affected session will pin it down
  (Verbose Logging on, send the LogOutput.log)

# 1.2.3 — first stable release

- Fixed doors and cupboards drifting out of sync with the host: the door's own auto-close
  spring (which runs locally) counted as "local motion" and blocked syncing — an open
  cupboard would quietly close itself on your screen while staying open for the host.
  The host's door angle is now continuously authoritative; only YOUR pushes, grabs and
  the settle right after them go local (so opening things stays instant)
- Known issue: doors/cupboards can still occasionally appear desynced from the host —
  this is ongoing work. Reports with Verbose Logging on are very welcome

# 1.2.2

- Thrown objects no longer snap to the host's landing spot after they fall: for 3 seconds
  after a throw the "wedged object" teleport is disabled (bounces legitimately land in
  different spots on each machine), and settling toward a resting host copy is capped to
  a calm glide speed instead of a zip

# 1.2.1

- Thrown objects no longer stop mid-air "catching their breath": the sync target is now
  led by your one-way ping (the host's copy always trails a moving object by exactly
  that), and after the throw grace corrections fade in gradually — the object follows
  the host's flight smoothly instead of braking toward its trailing position
- Fixed weird shop doors: releasing a door was going through the throw pipeline
  (throw grace + host-state overwrite), which masked the host's true door state — a
  locked shop door would reconcile toward where YOU left it instead of where the host
  keeps it. Doors are now fully exempt from throw handling

# 1.2.0

- The client controls what it touches: weapons, grenades and gadgets are back under
  local simulation for the true singleplayer feel — 1.1.9's blocklist made every tool
  you actively use feel host-laggy in hand. Weapon orientation is computed locally from
  each weapon's own aim/tilt tuning (no network data in the rotation loop, so none of
  the old glitching), gadgets mirror the host gently, and damage/explosions remain
  host-decided like vanilla
- Only genuinely self-moving objects stay on the game's normal sync: vehicles, drones,
  and the duck

# 1.1.9

- Adopted the original NetworkingReworked's item policy wholesale (credit: readthisifbad):
  weapons, powered gadgets (anything with a battery), grenades, mines, drones, upgrades
  and vehicles are never simulated — they use the game's normal sync, which is simply the
  reliable answer for objects with complex logic of their own. Correct orientation and
  behavior, at the cost of a small hand lag on those items only
- Simple items (health packs and plain carryables) keep the instant hand feel and
  predicted throws

# 1.1.8

- Fixed undrivable vehicles: the new drivable vehicles (Semiscooter etc.) were being
  synced toward the host's lagging copy while you drove them. Vehicles are now fully
  excluded from the simulation — they use their own driving physics and networking
- Fixed glitchy weapon hold: holding the host's rotation at full strength telegraphed
  every 10-per-second, ping-late network step. Guns and melee weapons now compute their
  hold orientation locally from their own tuning fields (aim offset, tilt) — the same
  numbers their scripts use — so there is no network data in the rotation loop at all.
  Other gadgets keep a gentle host mirror that can't telegraph steps

# 1.1.7

- Tool hold is now firm, not floppy: pointing the grab controller at the right rotation
  (1.1.6) wasn't enough — the game's base orientation torque is deliberately gentle.
  Host-side weapons feel firm because their scripts stack a 2x torque boost and heavy
  rotation damping on top; the mod now applies that exact recipe (the gun's own values)
  while holding the host's rotation

# 1.1.6

- Tool straightening finally works at full strength: the game's grab system re-captures
  its orientation target from the object's current rotation every frame, which silently
  cancelled every previous attempt that steered the object directly. The mod now writes
  the grab controller's own target (the same channel weapon scripts use) with the host's
  straightened rotation — the game's own tuned torque does the driving

# 1.1.5

- Config rebuilt for humans: section "1. Main" has plain on/off switches (Instant Carts /
  Instant Doors / Instant Items / No Timeout Kicks / Smooth Enemies), "2. Fine-Tuning"
  holds the numbers with plain-language explanations (defaults are good), "3. Debug" has
  Verbose Logging for bug reports. NOTE: settings from older versions reset to defaults
  once (the old entries are ignored); re-apply any personal tweaks in the new sections

# 1.1.4

- Tool straightening is now strong: it was switching itself off whenever the host's copy
  rested (no packets = no hold, and the game's torque crept the tool crooked again). The
  host's straightened rotation doesn't expire like positions do — the hold is now
  constant while you carry the tool, with roughly twice the corrective strength

# 1.1.3

- Held tools now STAY straight: the game's default grab torque wants a different
  orientation and was shoving the tool off the host's straightened rotation. We now do
  what the game's own weapon scripts do while imposing orientation — neutralize that
  torque and heavily damp rotation — then hold the host's rotation unopposed. Manually
  rotating a held item still takes priority, exactly like the game's gun code
- Completed the full study of the original NetworkingReworked (credit: readthisifbad):
  every technique catalogued in docs/NETWORKINGREWORKED-NOTES.md with adoption status

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
