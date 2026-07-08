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
