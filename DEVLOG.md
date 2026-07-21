# RevivalSync — Development Log & Hard-Won Lessons

Reference for contributors (and future debugging). Chronological-ish, distilled.

## Lineage
- Successor/merger of two mods: **NetworkingRevived** (rewrite of the abandoned
  `readthisifbad-NetworkingReworked`) and **NetworkTweaksRevived** (rewrite of
  `BlueAmulet-REPONetworkTweaks`). Decompiled sources of both originals were studied heavily.
- Why the originals broke: the game's overcharge update added a 6th field
  (`physGrabBeamOverCharge`) to PhysGrabber's photon stream (NetworkingReworked re-wrote that
  stream with 5 fields = packet corruption); RPC security guards (`SemiFunc.MasterOnlyRPC` /
  `OwnerOnlyRPC`) were added; IL-pattern transpilers died on recompiled code.

## Architecture (from the original NetworkingReworked — the thing that made it feel great)
- **Passive shadowing**: every eligible PhysGrabObject is locally simulated from spawn;
  while not locally held it is continuously blended toward the host's state (~0.075/tick).
  No puppet<->simulation handoffs — handoff seams were the source of most desync bugs.
- Local grab = instant authority: add self to `playerGrabbing` immediately, flip the private
  `isMaster` field so `PhysicsGrabbingManipulation` runs, dedupe the host's later broadcast.
- Host state captured at **`PhotonNetwork.OnSerializeRead`** (raw event array; data[0]=viewID,
  component data from index 3; pattern-scan for [bool,bool,bool,V3,V3,V3,V3,Quat] which
  self-aligns past other components' segments). Per-component capture proved unreliable.
- `PhotonTransformView` suppressed for simulated objects (Update prefix skip; serialize read
  = consume-and-discard 8 values to keep the stream aligned).
- Excluded from simulation: enemies, tumbled players. Shop items (ItemAttributes) were excluded until 1.0.7 - that made tools/weapons feel laggy in hand (host-driven + Smoothing delay stacked); now simulated like valuables (config SimulateItems), item LOGIC stays host-gated.

## The two foundational platform gotchas (cost days)
1. **Never touch OR Harmony-patch `PhotonNetwork` during plugin load.** Its static ctor spawns
   the PhotonMono dispatcher; created during chainloading it silently kills ALL connectivity
   (game hangs at "connecting", zero errors — looks like no internet). Even patching one of
   its methods triggers the ctor. Fix: defer via `PhotonHandler.Awake` postfix
   (`PhotonReady` flag) + lazy `EnsureCapturePatch()`.
2. **This game never runs Update/FixedUpdate on mod-created GameObjects or on the BepInEx
   plugin component** (proven with filesystem markers). Every "driver loop" before v1.0.2 was
   silently dead — the entire passive-sync machinery never executed; only Harmony patches ran.
   Fix: drive loops via postfixes on `GameDirector.Update` (frame) and
   `PlayerController.FixedUpdate` (physics), with frameCount/fixedTime double-run guards.
   Corollary: all pre-1.0.2 "this feature doesn't work" verdicts were meaningless.
   Also: AsyncLoggers buffers the log — hard-killing the game eats the tail; verify with
   graceful close or file markers.

## Sync-tuning lessons
- **Photon sends only on change**: no packets = host copy at rest exactly at last hostPos.
  Blending toward stale velocities makes objects vibrate/drift ("choppy"). `hostIdle`
  (packetAge > 0.35s) => zero target velocities & settle; while flowing, lead the target by
  packetAge. Use `MovePosition/MoveRotation` for blends (interpolation-aware); raw
  `rb.position` writes render as stepping (keep them only for snaps).
- **Held drift correction must be velocity-aware**: the host's copy ALWAYS trails a dragged
  object by speed*lag; correcting that trail = permanent brake (the "ultra slow cart").
  Allowance = speed*0.4, target led by hostVel*0.15; handback (vanilla control until re-grab)
  when truly diverged.
- **Carts aren't moved by grab forces when handle-held** — `CartSteer` directly drives
  velocity toward a follow point (5/7 m/s caps) and disables grab forces
  (`OverridePhysGrabForcesDisable`); its client-side preconditions are unreliable, so the
  drive is replicated locally — but ONLY for handle grabs (grab-area list check): body/"weak"
  drags are raw-force on the host and steering them locally desyncs hard.
- **Cart cargo riding a locally-held cart must be exempt from passive sync** (riding markers
  via `itemsInCart`), or it anchors the cart and tunnels through walls.
- **Doors**: vanilla clients destroy the HingeJoint and the host force-flags doors kinematic
  (`KinematicClientForce` each tick). Simulating them = keep the joint (operand-swap the
  `SemiFunc.IsNotMasterClient` call in `PhysGrabHinge.Awake`), make it unbreakable (host
  decides breaks; mirror the `broken` field), set `pgo.spawned=true` (gates hinge logic),
  and run the game's OWN hinge logic locally via the authority transpiler on
  `PhysGrabHinge.FixedUpdate`. The "door spring" is actually host-gated close-torque code
  in that method — don't fight it, run it.
- Stuck-in-geometry objects need a timed Snap (blending can't push through walls).
- kinematicClientForced pollutes streamed `isKinematic` — ignore it for held-object logic.

## Ops
- Build: `dotnet build -c Release` (game/BepInEx paths via gitignored `LocalPaths.props`).
- `update-package.ps1` = build + refresh `package/` + local TMM profile + upload zip in `bin/`.
- Publish: bump version in Plugin.cs + manifest.json + csproj + CHANGELOG, then upload zip
  manually OR `git tag <version>; git push --tags` (GitHub Actions -> Thunderstore; token
  lives ONLY in the repo's Actions secret THUNDERSTORE_TOKEN).
- Thunderstore rejects duplicate version numbers. Community slug: `repo`, team: `Revival`.
- Verbose diagnostics: `[reg] [mode] [held] [snap] [throw] [dedupe] [stats] [hinge]` — note
  saved configs keep old values; changed defaults only apply to fresh installs.
- Suspected third-party interference historically: discjenny-CartSpeedSync (removed),
  GodCommands (untested variable).

## 1.0.6 — velocity-steering, not position-forcing (the vibration lesson)

Symptom: everything vibrating "like a phone at 300%" while synced (packets healthy, no
errors). Cause: TickShadow ran MovePosition + velocity writes on DYNAMIC rigidbodies every
physics tick. MovePosition on a dynamic body teleports it each step, fighting its own
physics integration 50x/s, while the blend target itself resets with every packet
(lead = hostVel * packetAge sawtooths) and with Photon clock noise in the lag estimate.
Their RateSmoothing=1 config experiment did the same to the Hermite rate estimate for
non-registered objects (enemies/players) — every measured interval REPLACED the estimate.

Fix (the rule to keep): corrections steer VELOCITY (fold position error into the velocity
target: desiredVel = targetVel + clamp(posErr * gain)), with a 2cm/1° deadband so settled
objects never chase packet noise. Position forcing (MovePosition lerp) only for >1.5m
divergence (convergence guarantee) and host-kinematic objects. Held drift corrections are
acceleration nudges (velocity += clamp(err,3) * 2.5 * dt). Lag estimate clamped to 0.3s.
Post-throw: velocity blend drops to 0.08/tick or it would yank predicted throws toward
stale host velocity.

## 1.1.0 - weapon straightening + convergence, stable release

Held weapons stayed sideways for clients: item scripts call PhysGrabObject.TurnXYZ
(orientation targets) behind IsMasterClientOrSingleplayer gates. The torque APPLICATION
already ran locally (PhysGrabObject.FixedUpdate is transpiled) - only the target-setting
calls were gated out. Fix: transpile ItemGun.UpdateMaster + ItemMelee.FixedUpdate +
ItemMelee.TurnWeapon (orientation/override-only methods - verified in decomp that
shooting/misfire/battery/damage all live in separately-gated methods that stay host-only).
Ten other gadget classes call TurnXYZ with per-class structures; instead of transpiling
them all, held items without ItemGun/ItemMelee mirror the HOST's rotation while held
(mirrorHeldRot, slerp 0.12/tick, 2-degree deadband, skipped while heldHostIdle) - the host
runs their scripts on our behalf, so its rotation IS the straightened answer.

Convergence: removed the far-branch MovePosition entirely (jiggle source); one
distance-scaled velocity law now covers all divergences: gain = a*25*(1+2*errMag),
correction clamped 10 m/s. Multi-meter gaps close in ~0.4s, smoothly. Position writes on
dynamic bodies remain ONLY in Snap() and the host-kinematic branch (correct usage).

## 1.1.1 - the weapon-orientation lesson (do NOT transpile item scripts)

1.1.0 shipped two approaches side by side and the playtest was a perfect A/B:
- ItemGun/ItemMelee orientation methods transpiled to run locally -> violent vibration
  ("fighting with the host" feel, but it is actually fighting the CLIENT-side grab
  physics; plain-loot torque is weak so the same fight is invisible on valuables,
  weapon torque overrides are strong: OverrideTorqueStrength(2)+AngularDrag(20)).
- Everything else mirroring the HOST rotation while held (slerp 0.12, 2deg deadband,
  skip when heldHostIdle) -> perfectly fine.
Conclusion: mirror rotation for ALL held items; never run item orientation scripts
locally. The transpiler TargetMethods has a NOTE.

Throws: post-throw grace must be PURE local physics (early return before target math in
TickShadow, after teleport handling). Any correction during the grace drags the flight
toward stale "still in your hand" host data, and the host-kinematic branch could force
a fresh throw kinematic. Softened-blend variants (a*0.3, velBlend 0.08) were still too
strong once corrections became distance-scaled.

## 1.1.2 - what the original NetworkingReworked (0.2.2, readthisifbad) actually did

Decompiled it properly (scratchpad nr-decomp, regenerable: Thunderstore
readthisifbad/NetworkingReworked + ilspycmd). Techniques adopted with credit:

1. RELEASE: SyncAfterRelease/OverwriteStoredNetworkData rewrites the PTV network state to
   the local rb state at release -> no stale in-hand reference. Ours: EndLocalGrab seeds
   st.host* from rb + lastPacketTime=now. (They went further: DelayedReleasePatch skips
   GrabEnded, performs it locally, keeps streaming beam data so the HOST releases from the
   right spot - PreventPhysGrabBeamDeactivatePre. Not adopted; RPC-shape risky post-security-update.)
2. DOORS: full local reimplementation of PhysGrabHinge.FixedUpdate on clients, NO continuous
   rotation sync; their clients SENT OpenImpulseRPC/CloseImpulseRPC to everyone (would likely
   trip modern SemiFunc.MasterOnlyRPC guards - the game's own CloseImpulseRPC now checks it).
   Ours: local motion owns the door; follow host only when host moves and we rest.
3. THEY NEVER SIMULATED TOOLS: BlockedItems = ItemBattery/ItemGun/ItemRubberDuck/
   ItemGrenade*/ItemDrone*/ItemUpgrade* + enemies + tumbled players + hinges(from ownership).
   Their tools were vanilla-laggy but stable. We simulate them with position-local +
   host-rotation mirror via ANGULAR VELOCITY steering (never MoveRotation - fights grab torque).
4. Their ownership core: PhotonView.IsMine postfixed to true for simulated views + transpilers
   rewrote even isMaster FIELD READS to photonView.IsMine (OverrideTimersTick transpiler).
5. Melee swings: ItemMelee.OnPhotonSerializeView prefix consumes isSwinging stream and calls
   ActivateHitbox() on swing start - the answer if melee hits ever feel wrong for clients.
6. Their passive sync = raw rb.position/rotation/velocity lerps at 0.075, gated off while
   locally grabbed or riding a held cart; release used staged coroutine re-sync (SlowSyncCartRoutine).

### Correction to point 5 above (checked, do not re-chase)
The melee swing patch CANNOT and NEED NOT be ported: ItemMelee.OnPhotonSerializeView and
the isSwinging field no longer exist in the current game. Melee swings are now synced via
StateSetRPC (host -> RpcTarget.All, MasterOnlyRPC-guarded); StateSwinging calls
ActivateHitbox() from the UNGATED Update() half, i.e. hitboxes already activate on every
client natively. If melee hits ever feel wrong for clients, the suspect is hitbox POSITION
(host sees the weapon trailing our hand by ~ping), not activation.

## 1.1.6 - held-object orientation: retarget the controller, never steer the body

Four versions of tool-straightening attempts, one root cause found in the decomp
(PhysGrabber beam update, ~line 1981): while an object is held, the grabber RE-CAPTURES
cameraRelativeGrabbedForward/Up from the object CURRENT rotation every frame (when
physRotatingTimer <= 0). Consequences:
1. By default nothing straightens a held object - the orientation target follows the
   object, so the grab torque is orientation-neutral. Item scripts create real targets
   by overwriting these vectors every Update via TurnXYZ.
2. Any outside write to rb.rotation/angularVelocity gets neutralized or stomped by the
   grab manipulation running in the same tick - MoveRotation slerp (buzz), angVel
   steering (weak), angVel + torque overrides (still weak) all failed identically.
THE way to control held orientation: write grabber.cameraRelativeGrabbedForward/Up
(camera-relative: cam.InverseTransformDirection(targetRot * Vector3.forward/up), cam =
playerAvatar.localCamera.GetOverrideTransform()) every FRAME (frame-rate beats the
frame-rate recapture; tick-rate alone loses whole frames). Respect grabber.isRotating.

### 1.1.7 addendum to the orientation lesson
The complete held-orientation recipe is THREE parts (all from the game's own weapon code):
1. target: grabber.cameraRelativeGrabbedForward/Up = cam.InverseTransformDirection(rot * fwd/up)
2. strength: pgo.OverrideTorqueStrength(2f) sustained - base torque is scaled 15*dt (gentle)
3. damping: pgo.OverrideAngularDrag(20f) sustained - kills the flop
Target without strength+damping = floppy (1.1.6). Steering the rigidbody at all = stomped (1.1.2-1.1.4).

### 1.1.8 final orientation architecture + vehicles
- Weapons (ItemGun/ItemMelee): hold orientation computed LOCALLY per frame+tick from
  their own public tuning fields (gun: aimVerticalOffset; melee: forwardTilt/
  orientationOffset/turnWeapon/currentYRotation via FieldRef) -> pgo.TurnXYZ + the item
  type-appropriate strength (gun 2/drag 20, melee 0.4/drag 5). Zero network in the
  rotation loop. Mirroring the HOST rotation at full strength telegraphs the 10Hz,
  ping-late packet steps = "glitchy" (1.1.7 lesson). Gentle mirror stays for gadget
  types we do not know fields for.
- Drivable vehicles (ItemVehicle, e.g. Semiscooter) are EXCLUDED from simulation
  entirely - they have their own drive physics/networking; syncing them while the local
  player drives makes them undrivable. Old-mod philosophy: block what has complex logic.

## 1.1.9 - the item policy, settled (NR was right)

Weapons and powered gadgets are NEVER simulated. Full NR blocklist adopted: ItemVehicle,
ItemBattery (covers guns/melee/staffs/toggles), ItemGun, ItemMelee, plus name-prefix
ItemGrenade*/ItemDrone*/ItemUpgrade*/ItemMine*/ItemRubberDuck*. They are vanilla:
host-driven, correct orientation/behavior, small hand lag. Only simple carryables
(health packs etc.) get instant-feel simulation. The whole 1.1.0-1.1.8 orientation saga
existed because we owned objects whose per-type scripts fight generic ownership - NR
knew this in 0.2.x. The gun/melee local-orientation code paths remain in
ApplyHeldOrientation but are unreachable (blocked types never register); kept as
reference with this note.

## 1.2.0 - item policy final form: "the client controls what it touches"

1.1.9 (full NR blocklist) was stable but made every actively-used tool feel host-laggy -
the user immediately wanted singleplayer feel back. Settled policy:
- SIMULATED with local orientation: guns (aimVerticalOffset), melee (forwardTilt/
  orientationOffset/currentYRotation) - the 1.1.8 architecture, which was the correct
  design all along (no network in the rotation loop); it just never got a clean test
  before the blocklist pivot.
- SIMULATED with gentle host mirror: other gadgets (staffs, trackers, health packs...).
- VANILLA (blocked): only AUTONOMOUS objects - things that move THEMSELVES rather than
  being moved: ItemVehicle, ItemDrone*, ItemRubberDuck*. This is the principled line:
  ownership belongs to whoever provides the motion. Player provides motion -> client
  owns. Object provides motion -> host owns.

## 1.2.1 - throw follow + doors exempt from throw handling

- Mid-air "catching its breath" root cause: the host copy of a moving object trails by
  one-way ping; our lead only compensated packetAge. Fix: lead = min(packetAge +
  oneWayPing, 0.35), oneWayPing = PhotonNetwork.GetPing()*0.0005 refreshed per tick.
  Plus postThrowRamp (0.4s): after the throw grace, correction gain fades in from 0 -
  the object FOLLOWS host velocity first, converges second. Snap-distance check skipped
  while ramping (a hard throw diverging early must glide, not teleport).
- Hinges are exempt from the entire throw pipeline (no LocalThrow, no postThrow grace,
  no host-cache seeding on release): seeding masked the host TRUE door rotation - shop
  doors reconciled toward the player lie instead of the host locked/closed state.

## 1.2.3 - the door-sync rule, final form

"Local motion owns the door" (1.1.2) was too broad: the game AUTO-CLOSE spring runs
locally (authority transpiler) and its motion also gated out syncing - open cupboards
quietly self-closed on clients (desync). Final rule: HOST ANGLE IS CONTINUOUSLY
AUTHORITATIVE for hinges; only OUR interactions interrupt it - localPushTimer (contact
pushes), hingeSyncPause 1.5s on grab release / 1s trailing a push, and during the pause
only while still actually swinging (angVel gate INSIDE the pause window only). The
door spring may never out-vote the host.

## 1.2.5 - held-object snap discipline + session log archive

First real 4-player lobby ran ~10x the packet traffic of any test (2300+/10s); held-cart
trails legitimately hit 3.8m at sprint. Consequences fixed:
- Held wedge-snap ("freeing to host position") false-positived on a paused cart with a
  normal trail -> cart teleported OUT OF THE PLAYER HAND. Rule: never wedge-snap a held
  cart (velocity-driven, converges by itself; ForceHandback is the real safety); other
  held items need drift>2.5m AND speed<0.5 for 1.5s.
- Cart handback threshold x1.5 and 1.2s dwell (busy-lobby headroom).
- ArchiveSessionLog: BepInEx truncates LogOutput.log every launch and wiped a bug
  session once. Plugin copies the live log to BepInEx/RevivalSync-logs/session-*.log
  every 60s + on OnDestroy, keeps newest 10. File.Copy works while BepInEx holds the
  file (FileShare.Read).

## 1.2.7 - drone handoff + the all-switches-off incident

- July 18 sessions ("doors are shit again + insane desync"): the log archive earned its
  keep immediately - headers showed SmoothSync: False, and the config had ALL FOUR main
  switches off (user testing toggles). Most of the report was vanilla behavior. Rule for
  future triage: CHECK THE LOADED-LINE + CONFIG STATE FIRST before hunting regressions.
- Feather/magnet drones: drone applies OverrideMass(0.5)/OverrideZeroGravity/drag to its
  magnetTargetPhysGrabObject HOST-SIDE only -> our full-weight local sim vs floating host
  copy = "immovable object". Fix: droneExempt state - while any ItemDrone with
  magnetActive targets a registered pgo (fields internal, FieldRefAccess, synced to
  clients via RPCs), that object reverts to vanilla sync (IsSuppressed/IsRegistered/
  HasPhysicsAuthority all false, StartLocalGrab refuses, Restore() seeds PTV on
  transition). Consistent with the ownership rule: the DRONE provides the motion.

## 1.2.8 - drone flap root cause + resync key + convergence backstop

- 1.2.7 drone exemption looped 969x on one piano: (a) Restore() ends in HardRemove -
  it UNREGISTERS (know your own primitives!); exempt-enter removed the state, sweep
  re-registered, fresh state exempted again. Split: SeedPtvFields (fields only) vs
  Restore (seed + remove). (b) Deeper design bug: exempting a LOCALLY HELD object
  fights the player grab (feather drone = help the player carry). Rule: player grab
  wins; while held + feather-targeted, replicate the drone target physics locally
  (OverrideMass 0.5 / Drag 1 / AngularDrag 5 per ItemDroneFeather); drone-only ->
  vanilla handoff.
- ResyncAll (NR HardSync for everything, credited) on configurable key (default F8).
- Convergence backstop in TickShadow: >1.5m from target for >5s -> Snap. Promise:
  loot always ends up where the host sees it.
- UnityEngine.Input needs UnityEngine.InputLegacyModule reference (type-forwarded).

## 1.2.9 - the override gate + bounded cargo (113 F8 presses of evidence)

- PhysGrabObject.OverrideTimersTick has an INTERNAL master gate (line ~617) right before
  OverrideVariousTick/OverrideStrengthTick/mass consumption: EVERY Override* call on a
  client has been a silent no-op this whole time (feather mass, tool torque/drag boosts,
  hinge drag). NR transpiled exactly this method - now understood WHY, and adopted
  (CartAuthorityPatch target). Lesson: when replicating game behavior locally, verify
  the CONSUMING code path runs client-side, not just the setter.
- Cart cargo (ridingTick) had NO safety net: exempt from blends, backstop, wedge, snap.
  Busy lobby = carts always in use = most loot permanently exempt = unbounded drift =
  "insane desync", and the 5s backstop could never fire for exactly the objects that
  needed it. Riders now Snap at >3m from hostPos (bounded riding).
- Auto Resync Seconds config (0=off): automated ResyncAll for players who want it.

## 1.2.10 - the inside-the-tolerance desyncs

Log pattern to remember: cargo-stray fired 1x, backstop 0x, yet the player saw stuck
loot everywhere -> the desync lived INSIDE the tolerances:
- Riding cargo (pure local): bumps ejected loot locally to lie BESIDE the cart, under
  the 3m bound, uncorrected forever. Fix: gentle keep-in-basket velocity pull beyond
  0.3m (gain 2/s cap 2m/s blend 0.3), snap at 2m.
- rb.IsSleeping() early-return in rest-settle let objects that fell asleep in the WRONG
  place sleep forever, immune to every correction. Fix: only skip when within 0.5m of
  hostPos; otherwise WakeUp and glide home. (Sleep = the great correction-bypass.)
- Drones: blanket blocklist made CARRIED drones vanilla-laggy (the old shop-item lag).
  Now registered like items; dynamically droneExempt while toggleState on (deployed,
  flies itself) or while magnet-carrying. ItemToggle.toggleState is public.

## OPS GOTCHA - verify the profile DLL version after every deploy
update-package.ps1 reports "local profile updated" even when the copy is blocked by the
GAME HOLDING THE DLL LOCK. 1.2.10 sat un-deployed for a full cycle (profile stuck on
1.2.9) while I thought fixes were live - the user replayed the SAME 1.2.9 session 3x.
ALWAYS confirm after building while a session may be open:
  [Diagnostics.FileVersionInfo]::GetVersionInfo("<profile>\Revival-RevivalSync\RevivalSync.dll").ProductVersion
and cross-check the log's "RevivalSync X.Y.Z loaded" line against the version you expect
BEFORE diagnosing "it is still broken". Same-version + same-log = not tested yet.

## 1.2.11 - CARGO MUST BE CORRECTED IN THE CART FRAME, NEVER IN WORLD SPACE

First real 1.2.10 log. Cart cargo never snapped/strayed (bounds looked fine) yet loot
"stuck out and fell out, like it is slippery". Cause was my own 1.2.10 keep-in-basket
pull: it pulled cargo toward st.hostPos (WORLD). The host copy of a moving cart trails
ours by one-way ping, so its cargo positions trail too - the pull therefore shoved every
item BACKWARD relative to the basket, every tick, ejecting it over the rim. More speed =
more ejection.
RULE: an object carried by another object is only meaningfully "in the right place"
RELATIVE TO ITS CARRIER. Correct in the carrier frame:
  hostLocal = Inverse(cart.hostRot) * (rider.hostPos - cart.hostPos)
  want      = cart.rb.position + cart.rb.rotation * hostLocal
Lag cancels out entirely. >1.5m err = place it back (with cart velocity, not zero);
>0.15m = soft velocity settle (6/s, cap 4, *dt). SimState.ridingCart holds the carrier.
Same rule will apply to any future carrier (conveyors, vehicles, players).

Also: host sets the PTV teleport flag continuously on some objects (death heads: 3710
snaps in one session, 1096 at dist<0.05m). Ignore teleport flags when we are already
within 0.1m - pure spam + needless physics writes.

## 1.2.12 - cargo: stop correcting it (NR was right), and the pooling trap

CART CARGO, SETTLED. Three generations of correction all failed differently:
  1.2.4  blend toward host       -> rattle/vibration
  1.2.10 world-space pull        -> shoved loot backwards out of the basket (host cart
                                    trails by ping, so its cargo does too) = "slippery"
  1.2.11 cart-frame pull         -> fights the basket own colliders, still annoying
NR did NONE of it: ApplyPassiveSync skips entirely when (IsItemInCart && CartIsBeingHeld).
Cargo rides local physics in a local basket with local collisions - the basket holds the
loot, the network does not have to. Reconcile only when the haul ends (NR:
SlowSyncCartRoutine, cart first then items staggered 0.02s at 80% duration; ours: correction
ramp 0.5s + (n&7)*0.05 stagger). RULE: do not network-correct an object that is resting
inside another simulated object.

POOLING TRAP: AssetManager.physDisabledPosition = (0,3000,0). The game parks deactivated
objects there and the host STREAMS that position. Blending toward it flings our copy
skyward -> "beyond snap distance" -> snap back -> repeat (272x on scenery in one session,
same few decoration valuables). Guard: skip sync when hostPos is within 5m of the parked
position. Watch for this class: host positions that are not real gameplay positions.

## 1.2.13 - passive sync reverted to NetworkingReworked's four lines

TickShadow correction had grown to ~80 lines / 8 interacting mechanisms. NR's entire
passive sync (FakeOwnershipController.ApplyPassiveSync) is:
    rb.position        = Lerp (rb.position,        state.position,        0.075)
    rb.rotation        = Slerp(rb.rotation,        state.rotation,        0.075)
    rb.velocity        = Lerp (rb.velocity,        state.velocity,        0.075)
    rb.angularVelocity = Lerp (rb.angularVelocity, state.angularVelocity, 0.075)
...guarded only by !locallyGrabbed && (!inCart || !cartHeld). That is all of it.

WHY OURS KEPT FAILING: every refinement (ping+age extrapolation, hostIdle, deadbands,
error-scaled velocity steering, corrCap, wedge timers) made the TARGET MOVE BETWEEN
PACKETS. A moving target + per-tick correction = the fight that showed up as vibration,
"weak" holds, slippery cargo, mid-air braking. NR's target is stationary between packets,
so a plain lerp is an exponential decay: smooth, convergent, unable to fight physics.
Also reverted CacheHostState to store the RAW streamed position (NR read the direction
slot and ignored it) - the dir*lag lead was the same moving-target bug at the source.

KEPT deliberately (not in NR, earned here): pooled/parked-object guard (y=3000),
HostStalled guard, hostKinematic follow, hinge system, throw grace + ramp, cargo riding,
snap distance, 5s convergence backstop, F8 ResyncAll.
RULE GOING FORWARD: if a fix requires the sync target to move, it is the wrong fix.

## 1.2.14 - held objects go NR-simple too; full NR audit

NR held rule: ApplyPassiveSync is skipped entirely when locally grabbed. NO corrections.
Ours had a drift nudge (fought the natural ping-trail = the "ultra slow cart" brake) and
a wedge-snap (mistook a paused cart trail for stuck = "cart teleported out of my hand").
Both removed. Kept ONE last resort: ForceHandback when drift > HandbackAt for >0.6s
(1.2s carts) - NR had nothing, but without it a snagged host copy leaves you holding a
ghost forever. Config "Held Object Correct At" deleted (dead).

### AUDIT: where we are vs NR, and why we do NOT copy the rest
ALREADY NR (verbatim or equivalent): passive sync (1.2.13), cargo rule (1.2.12), held
rule (1.2.14), release state-overwrite, door local-logic model, cart transpilers,
OverrideTimersTick transpile, autonomous-object blocking.
DELIBERATELY NOT NR - copying these would REGRESS the mod on the current game:
 - Capture: NR used fixed offsets (data.Length==15, offset 7). THE GAME UPDATE CHANGED
   THE PAYLOAD - this is precisely why NR is dead. Ours pattern-scans and survives.
 - Hinge impulse RPCs: NR had CLIENTS broadcast OpenImpulseRPC/CloseImpulseRPC to All.
   Modern equivalents are SemiFunc.MasterOnlyRPC-guarded; a client broadcast is rejected
   and is exactly the shape the security update targets.
 - PhotonView.IsMine postfix -> true: flips RPC targeting and serialization DIRECTION,
   not just physics authority. Ours flips PhysGrabObject.isMaster + targeted transpiles.
 - DelayedRelease beam replay (skip GrabEnded, replay beam state, client-dispatched
   GrabEndedRPC/PhysGrabBeamDeactivateRPC): same RPC-shape risk.
NETWORKTWEAKS VERDICT: the whole "tweaks" half is 3 lines and ZERO patches
(DisconnectTimeout=3600000, SentCountAllowance=10000, MinimalTimeScaleToDispatchInFixed
Update=Inf). Keep - free, no conflict surface, prevents random lag-spike kicks.
Smoothing.cs (325 lines, Hermite for non-simulated objects) is the only optional bulk;
NR had no equivalent (it replaced PhotonTransformView.Update wholesale instead).

## 1.2.15 - the cart cluster: local cart logic is held-only

Log state: 25 snaps/session (vs 3714 four days ago) - core NR sync is holding. The
remaining complaints were all cart:
- 90-degrees-off cart: MasterOrLocallySimulated let PhysGrabCart.FixedUpdate (stabilize/
  state/steer) run for EVERY registered cart. For a shadowed cart that local logic
  fights the passive rotation slerp indefinitely; and the backstop measured only
  DISTANCE, so a right-place-wrong-angle cart was never rescued. Fixes: (a) wrapper
  returns false for PhysGrabCart contexts unless locally grabbed; (b) backstop triggers
  on dist>1.5m OR rotErr>30deg (resets <0.75m AND <10deg).
- Loot slides off: riding condition (velocity threshold) flapped at stop-start; every
  riding<->synced transition jostled the load. rideHoldTimer 1.5s hysteresis on the
  CART state keeps riders marked through pauses.
- Host loads loot into unsynced cart -> sticks OUTSIDE the basket: host cargo spots land
  outside our misrotated basket; downstream of the rotation fix.
RULE (generalizes 1.2.12's): run the game's per-object master logic locally ONLY for
objects the player is actually holding; for shadowed objects it fights the sync.

## 1.2.16 - the upgrade thief: stale playerGrabbing entries

Player report: hit someone with an upgrade orb, they use it later (or do anything),
and the upgrade lands on the REPORTER. Root cause chain (all vanilla facts verified in
decomp):
- Upgrades apply to PlayerAvatarGetFromPhotonID(ItemToggle.playerTogglePhotonID); that
  ID comes from whichever CLIENT fired ToggleItem, and ItemToggle.Update fires it when
  physGrabObject.heldByLocalPlayer && InputDown(Interact). So attribution is decided by
  a client-side "am I holding this" flag plus the E key.
- heldByLocalPlayer is recomputed every FixedUpdate purely from the playerGrabbing
  list (any entry with photonView.IsMine).
- Our GrabStartedPatch adds the local grabber to playerGrabbing instantly (and the
  dedupe patch suppresses the host's add broadcast), but GrabEndedPatch removed it ONLY
  when IsLocalGrab was still true. An UNSEEN release (orb knocked from hand by hitting
  a player, tumble, death) makes TickHeld self-heal localGrab=false first; the later
  GrabEnded then early-returned and the entry was NEVER removed by us.
- Vanilla's janitor (PhysGrabObject.Update) drops entries only when the grabber's
  GLOBAL grabbed flag is false - and you are almost always holding SOMETHING - so the
  stale entry survived. Result: every local E press toggled that orb from anywhere on
  the map with OUR photon ID; it popped in the other player's hands credited to us.
  ("They use it and I get it" was actually keyed to OUR E presses - E is pressed while
  holding an item, which is exactly when the janitor cannot clean.)
Fix, three layers:
1. GrabEndedPatch removes our entry UNCONDITIONALLY (before the IsLocalGrab return).
2. EndLocalGrab calls ScrubStaleLocalGrabber (covers the unseen-release self-heal).
3. Tick calls the scrub for every non-localGrab state (safety net; genuine grabs have
   localGrab set synchronously by the GrabStarted postfix, so no false positives).
Scrub condition: local entry whose grabber is not (grabbed && grabbedPhysGrabObject ==
this). Verbose log tag: [stale-grab] - its presence in a session log CONFIRMS this
mechanism fired.
RULE: any list WE insert into, WE must remove from on every exit path - vanilla
cleanup loops are written for vanilla insertion patterns and will not cover ours.

## 1.2.17 - upgrade attribution: stay out of it (1.2.16 was the wrong call)

User pushback on 1.2.16: "that was not what was happening bro i kept holding it".
They were right. Evidence gathered AFTER shipping 1.2.16 (do this first next time):
- Grab/release counts on Item Upgrade* across all 10 archived sessions: perfectly
  balanced (0/0, 1/1, 8/8, 7/7, 11/11, 24/24, 2/2, 23/23), zero handbacks. NOTE: this
  is NOT proof on its own - EndLocalGrab logs "Released grab authority" on the unseen-
  release self-heal path too, so balanced counts cannot rule a stale entry in or out.
- The real killer for the 1.2.16 theory: a stale local entry is SELF-CORRECTING.
  Vanilla GrabEndedRPC runs on the master, and if the master's list contains us it
  broadcasts GrabPlayerRemoveRPC to All - which cleans our local list within one round
  trip. We never suppress that RPC (dedupe only patches GrabPlayerAddRPC). So a
  PERSISTENT stale entry is close to impossible. 1.2.16 is defensible hygiene, not
  the bug. Keep it, do not claim it fixes upgrades.
ACTUAL MECHANISM (vanilla, verified in decomp):
- ItemToggle.Update fires on ANY client where physGrabObject.heldByLocalPlayer is true
  and Interact goes down. It sends ToggleItemRPC(toggle, SemiFunc.PhotonViewIDPlayer
  AvatarLocal()) to ALL. ToggleItemRPC has NO MasterOnlyRPC guard.
- ToggleItemLogic stores that id in playerTogglePhotonID; ItemUpgrade.PlayerUpgrade and
  every ItemUpgrade* subclass resolve the recipient from it
  (PunManager.Upgrade*(PlayerGetSteamID(PlayerAvatarGetFromPhotonID(id)))). PunManager's
  Upgrade* methods are plain local mutations of statsManager dictionaries - no master
  gate anywhere. Whoever's client fires FIRST owns the upgrade, permanently.
- heldByLocalPlayer is recomputed each PhysGrabObject.FixedUpdate from playerGrabbing
  (any entry with photonView.IsMine). Checked: no master-gated early return precedes
  that block, so our FixedUpdate transpile does NOT corrupt it.
- OUR effect: the instant-grab patch sets that flag with ZERO host round trip, while an
  unmodded player must wait for master GrabStartedRPC -> GrabPlayerAddRPC. On a
  CONTESTED orb (two players grabbing at it - exactly what handing one over looks like)
  the modded client wins the race every time and takes the upgrade.
FIX: CanSimulate returns false for ItemUpgrade (GetComponentInParent). Orbs are held at
the face via OverrideGrabDistance(0.5f) and need no instant physics, so there is nothing
to lose. Attribution reverts to pure vanilla.
DIAGNOSTICS added (verbose): [upgrade] on ItemToggle.ToggleItem (this client is claiming
it + SimManager.DescribeGrabState) and on ItemUpgrade.PlayerUpgrade (who the credit
landed on). If it recurs with these silent on our side, it is vanilla, not us.
RULE: never take local authority over an object whose game logic assigns PERMANENT,
attributable rewards from a client-side "who is holding this" check. Latency advantage
IS a behaviour change even when the physics are perfect.
OPS: do not bulk-edit source with PowerShell (Get-Content|-replace|Set-Content) - PS 5.1
Get-Content reads UTF-8 as ANSI and the round trip mangles every em dash. Caught it via
git diff --numstat showing 7 changed lines for a 1-line version bump. Use the Edit tool.

## 1.2.18 - full audit pass

Read every source file end to end (SimManager 1560, Patches 433, Plugin 307,
Smoothing 361) plus the build/publish scripts. Findings:
1. CRITICAL (self-inflicted, 1.2.17): ToggleClaimLogPatch and UpgradeCreditLogPatch were
   written but NEVER added to Plugin.patchTypes. harmony.PatchAll(Type) is called per
   entry of that array, so both diagnostics were dead code - they would have recorded
   nothing and the next log would have "proved" the wrong thing. FIXED.
   RULE: adding a patch class is TWO steps in this codebase - write it, then register it
   in patchTypes. Grep patchTypes against the [HarmonyPatch] classes after every add.
2. Diagnostics could throw into game code (SemiFunc.PhotonViewIDPlayerAvatarLocal
   dereferences PlayerAvatar.instance, null outside gameplay). Both wrapped in try/catch
   plus an explicit instance null check. A logger must never be able to break a frame.
3. .github/workflows/publish.yml carried the ORIGINAL Thunderstore description
   ("[HIGHLY EXPERIMENTAL - expect game-breaking bugs]"), contradicting manifest.json.
   A tag push would have published that text over the current one. Synced, with a
   comment tying the two together.
4. README did not document the F8 emergency resync / Auto Resync Seconds at all. Added.
Verified clean, no change needed: no token in any tracked file (git grep tss_);
.gitignore covers bin/obj, LocalPaths.props, *token*, reference/; ScrubStaleLocalGrabber
correctly SKIPS the handback case (grabber genuinely holding -> g.grabbed &&
grabbedPhysGrabObject == pgo) so it cannot strip a real grab; Tick iterates tickBuffer
(a snapshot) so Restore/HardRemove mutating states mid-loop is safe; SweepDead/
Smoothing.Sweep only ever hold Unity fake-null keys, never real nulls, so Remove cannot
throw; TransformViewSerializePatch and Smoothing.SerializePatch agree on __runOriginal
so the stream is never double-consumed; the cart guard in MasterOrLocallySimulated only
matches PhysGrabCart contexts, leaving PhysGrabObject.FixedUpdate/OverrideTimersTick
authority intact for shadowed carts as intended.

## 1.2.19 - resync key: string instead of KeyCode enum + second audit pass

User: changing the F8 resync key means scrolling a horrible list. Confirmed - their
config already showed "Resync Loot Key = F4", and REPOConfig renders a ConfigEntry
<KeyCode> (enum) as a REPOSlider stepping through all ~350 KeyCode names. A
ConfigEntry<string> renders as a typed REPOInputField instead (verified in the
decompiled REPOConfig ConfigMenu.cs: string -> CreateREPOInputField, enum ->
CreateREPOSlider over Enum.GetNames).
FIX: ResyncKey is now ConfigEntry<string> (default "F8"). Plugin.ResyncKeyCode parses
it once and caches until the text changes; KeyCode.None disables. ParseKey uses
Enum.TryParse (case-insensitive, covers every real KeyCode name incl. Mouse0-6,
Keypad*, F1-24) then a small alias table (spacebar, esc, ctrl, a bare digit -> Alpha#,
off/disabled -> None). MIGRATION: the old enum serialized as its name ("F4"), which is a
valid string, so existing bindings survive the type change untouched; BepInEx just
rewrites the type metadata and drops the acceptable-values list on next launch.
SELF-BUG caught in review: my first draft aliased "mouse3"/"mouse4"/"mouse5" to
Mouse2/Mouse3/Mouse4. Those are all valid KeyCode names, so Enum.TryParse matches them
FIRST - the aliases were unreachable AND remapped the number wrongly (Unity Mouse3 = a
side button, not middle). Removed; kept only non-enum spellings (middlemouse ->
Mouse2). RULE: before adding a case-insensitive alias, check it is not already a real
enum name, or the direct parse silently wins.
SECOND AUDIT (user "check everything again"): re-verified EVERY reflected member against
the current decompiled game, not just structure. All present with matching type:
PhysGrabObject.isMaster/isActive/heldByLocalPlayer/spawned/clientNonKinematic,
ItemToggle.playerTogglePhotonID, PhysGrabber.grabbedPhysGrabObject, PhysGrabCart.
itemsInCart/inCart/physGrabObjectGrabArea/isSmallCart, PlayerAvatar.isSprinting,
PhysGrabHinge.broken, ItemMelee.currentYRotation/usesForceRotation/forwardTilt/
customTorqueStrength, ItemGun.aimVerticalOffset, ItemDrone.magnetActive/
magnetTargetPhysGrabObject. CartAuthorityPatch targets exist (PhysGrabObject.FixedUpdate
line 939 / OverrideTimersTick 574, PhysGrabCart.FixedUpdate 263/CartSteer 313,
PhysGrabHinge.FixedUpdate 185, PhysGrabObjectGrabArea.Update 87). Override* signatures
match. ResyncKey was the ONLY enum config entry, so it was the sole scrolling-menu
offender; all other entries are bool/float (toggles/sliders).
NOTE (not changed): ItemHealthPack uses the SAME playerTogglePhotonID attribution as
upgrades, so in theory a contested health pack could heal the racer who grabbed first.
Left as-is: consumable not permanent, unreported, and exempting it would make health
packs feel host-laggy in hand. Documented so it is a known quantity, not a surprise.
