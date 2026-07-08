# NetworkingReworked 0.2.2 — complete technique reference

Full study of the decompiled original mod by **readthisifbad** (plugin GUID
`net.ovchinikov.nwrework`), whose techniques RevivalSync builds on — with credit and
thanks. Decompile locally at `reference/networkingreworked-decomp/` (gitignored, not
republished; regenerate with ilspycmd from the Thunderstore package).

Status legend: ✅ adopted in RevivalSync · 🔁 adopted in improved form · ❌ deliberately
not adopted (reason given) · 💤 obsolete (game changed).

## Ownership core

- **FakeOwnershipData** — static registries keyed by ViewID: simulated views, locally/
  network-grabbed, grab counts, recently-thrown timestamps, items-in-cart mapping, raw
  stream cache. 🔁 ours: per-object `SimState` with the same roles.
- **FakeOwnershipController** — MonoBehaviour added to every eligible PGO ~1s after
  `PhysGrabObject.Start` (skips hinges). Runs its own FixedUpdate for passive sync.
  ❌ as a MonoBehaviour: this game doesn't reliably run mod component lifecycles (our
  driver-hooks lesson); ours ticks from Harmony hooks instead.
- **PhotonView.IsMine postfix** returns true for simulated views — one switch that flips
  every ownership check in the game at once. ❌ too broad: also flips RPC-target checks
  and serialization direction decisions; post-security-update this risks tripping
  `SemiFunc.MasterOnlyRPC`-style guards. Ours flips only `PhysGrabObject.isMaster` plus
  targeted authority transpiles.
- **Authority transpilers** rewrite `PhotonNetwork.IsMasterClient` calls,
  `SemiFunc.IsMasterClientOrSingleplayer` calls, AND `isMaster` **field reads** to
  `this.photonView.IsMine` in: PhysGrabObject FixedUpdate/Update/**OverrideTimersTick**,
  PhysGrabCart FixedUpdate/Update/CartSteer/SmallCartLogic, GrabArea.Update,
  ImpactDetector FixedUpdate/Update/OnCollisionStay/OnTriggerStay. 🔁 ours does the call
  swaps by identity; field-read rewiring and ImpactDetector/OverrideTimersTick coverage
  are candidates if impact effects or override timers ever misbehave client-side.

## Blocking policy (their item answer)

`BlockedItems.IsBlockedType`: carts always allowed; blocked = enemies, tumbled players,
hinges (from ownership — doors handled by their own system), **ItemBattery, ItemGun,
ItemRubberDuck, ItemGrenade\*, ItemDrone\*, ItemUpgrade\*** (name-prefix scan).
**They never simulated tools** — tools were vanilla (host-driven, laggy but stable).
🔁 ours simulates items too (instant hand feel) with host-rotation mirroring for
orientation; their blocklist is the fallback if any item type proves unfixable.

## Grab flow

- `GrabPlayerAddRPC` prefix: register grabber counts; if the grab is ours →
  SetLocallyGrabbed + SimulateOwnership. Postfix: attach controller on demand; for carts,
  fix `PhysGrabber.initialPressTimer = 1f` (CartOwnershipFixer) — a cart-grab
  responsiveness lever we haven't needed yet. ✅ equivalent instant-grab via
  GrabStartedPatch + InstantCartHandle.
- `GrabPlayerRemoveRPC` prefix: unregister, mark recently-thrown, release local grab.

## DelayedRelease — the throw system (their crown jewel)

1. `GrabEnded` prefix returns false (skips original): performs the release **locally**
   (invokes private `Throw`, removes grabber), snapshots the beam state (puller pos,
   plane pos, mouse velocity, isRotating, colorState).
2. `PhysGrabber.OnPhotonSerializeView` prefix (StreamInjectPatch): next outgoing tick
   sends the **snapshotted** beam state instead of live data, then a coroutine (10 ms)
   sends `GrabEndedRPC` to the master + `PhysGrabBeamDeactivateRPC` to all.
3. `PhysGrabBeamDeactivate`/`...RPC` prefixes keep the beam alive until that dispatch.
4. Then `SyncAfterRelease` → **OverwriteStoredNetworkData**: rewrites ALL of the PTV's
   internal network state (m_NetworkPosition/receivedPosition/prevPosition/stored/
   smoothed/rotations/velocities/m_Distance/m_Angle/firstTake/teleport) to the local rb
   state, so nothing stale remains to blend toward.

Net effect: **the host releases the object exactly where the client saw it**.
✅ adopted the state-overwrite half (EndLocalGrab seeds cached host state from local rb).
❌ the beam-replay half: prefix-skipping GrabEnded + client-driven RPC dispatch is the
RPC shape most likely to trip modern security guards; revisit only if release-position
mismatch is ever the proven cause of a bug.

## Sync core (their PTV replacement)

- `PhotonTransformView.OnPhotonSerializeView` prefix — full rewrite. Master writes:
  isSleeping, teleport, isKinematic(|forced), velocity, angularVelocity, position,
  direction, rotation (the 8-slot pattern our capture scans for). Client reads: applies
  sleep immediately (zero velocities + Sleep), kinematic flag, position led by
  `|PhotonNetwork.Time − info.SentServerTime|`, auto-teleport at >5 m divergence,
  first-take hard snap.
- `PhotonTransformView.Update` prefix — client-side apply: `MoveTowards` network pos at
  `SerializationRate` step, MovePosition/MoveRotation, then velocity/angularVelocity
  apply; forces `RigidbodyInterpolation.Interpolate`; sleeping objects pinned kinematic.
  🔁 ours: suppression + SimManager blending (velocity-steered) for registered objects,
  Hermite Smoothing for the rest.
- `PhysGrabObject.OnPhotonSerializeView` prefix — PGO itself is ALSO an observed
  component streaming **rbVelocity, rbAngularVelocity, isSliding, isKinematic** (4 more
  slots; explains the 15-element payloads). Client consume keeps `lastUpdateTime` fresh.
  ✅ our pattern-scan self-aligns past these; noted in case isSliding sync ever matters.
- **PhotonStreamCache.Store recognized TWO component orderings**: `[b,b,b,V3,V3,V3,V3,Q]`
  and `[V3,V3,V3,Q,V3,V3,b,b]` — prefab-dependent observed-component order. Our scan
  handles arbitrary offsets of the first pattern; if a prefab ever uses the second
  layout, add it.
- **SerializeReading**: their `PhotonNetwork.OnSerializeRead` capture — applied lazily on
  `RunManager.Awake` (they hit the same static-ctor landmine we did!), but positional:
  `data.Length == 15`, offset 7. The game update changed the payload → silent death.
  🔁 ours pattern-scans and survives.

## Doors

- Full local **reimplementation** of `PhysGrabHinge.FixedUpdate` (prefix skip): hinge
  point rb stabilization (MovePosition toward hinge offsets by angle thresholds),
  close/latch state machine, auto-close torque, bounce impulses, closed force-shut
  (MovePosition to rest pose), `KinematicClientForce(0.1f)` called per tick, runs for
  master OR IsMine(faked). **No continuous rotation sync at all.** ✅ our equivalent:
  authority transpiler runs the game's own FixedUpdate + local-motion-owns-the-door rule.
- Open/close **events** re-broadcast by clients: their HingeRPC sends `OpenImpulseRPC` /
  `CloseImpulseRPC` with `RpcTarget.All` when the local door latches/pops. ❌ the modern
  game's impulse RPCs are `SemiFunc.MasterOnlyRPC`-guarded — a client's broadcast would
  be rejected (and is exactly the class of thing the security update targeted). Physics
  converge via the host's own simulation instead; only effects (sound/shake) arrive with
  the host's RPC.
- `PhysGrabHinge.Awake` transpiler: joint keeper — skips the `Object.Destroy` call by
  dropping "~5 instructions after Destroy" (positional, fragile). 🔁 ours operand-swaps
  the `IsNotMasterClient` gate (identity-based).
- `OnJointBreak` prefix: when the local joint snaps — counter-impulse the door body
  (−2× velocity force, −10× angular torque) to stop it flying, set `broken` locally;
  only the master triggers `HingeBreakImpulse` effects. ✅ ours mirrors breaks from the
  host instead (unbreakable local joint); their counter-impulse is the recipe if we ever
  let local joints break.

## Items / combat

- **ItemMelee.OnPhotonSerializeView** prefix: consume `isSwinging` stream, on rising edge
  set `newSwing` + `ActivateHitbox()` on clients. 💤 method and field no longer exist —
  the modern game syncs melee via `StateSetRPC` and `StateSwinging` calls
  `ActivateHitbox()` from the ungated `Update()` on every client natively.
- **HurtColliderPatch**: transpile the FIRST `IsMasterClientOrSingleplayer` in a
  HurtCollider method to `IsOwnerOrSingleplayer(gameObject)` — owner-side hit authority.
  ❌ for now (hit registration is host-side by design); the ready answer if melee hits
  whiffing at the host's trailing sword position ever becomes the confirmed complaint.

## Misc

- Their passive sync: raw `rb.position/rotation/velocity/angularVelocity` lerps at
  0.075/tick, skipped while locally grabbed or riding a locally-held cart; release used
  staged coroutine re-syncs (SlowSyncCartRoutine: cart first, then cargo staggered 0.02s,
  80% duration each). 🔁 ours velocity-steers (no transform forcing) with deadband.
- Cart cargo exemption keyed through itemsInCart mapping — same idea as our ridingTick.
- `RunManager.Awake` prefix clears all ownership state each run. ✅ ours re-registers per
  level via registration sweeps + mode transitions.
