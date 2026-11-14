# Physics coverage map

What RevivalSync predicts, what it deliberately hands back to the game, and what it has
never touched. Compiled by sweeping every networked physics class in REPO 0.4.4.3
(Assembly-CSharp) against the mod's actual behaviour.

Read this before adding any new object to the simulation, and before assuming a
"desync" report is a sync bug — several of the entries below are *by design*.

---

## 1. Predicted locally (the mod owns these)

Props and valuables, loot, **carts**, **doors/cabinets** (hinges), gadgets, grenades,
death heads. (Weapons were on this list until 1.5.4.) Anything with a `PhysGrabObject` + `PhotonTransformView` that is
not on the exemption list in `SimManager.CanSimulate`.

## 2. Deliberately handed back to vanilla

| Object | Since | Why |
|---|---|---|
| `ItemVehicle` | early | drives itself; host-only |
| `ItemDrone` (while switched on) | 1.2.8 | dynamic exemption — a switched-off drone in the hand is still predicted. Since 1.5.4 a switched-on drone is the host's even while held (`ItemDrone.FixedUpdate` is master-only) |
| objects a drone is carrying | 1.2.7 | the drone provides the motion; the local player's own grab still wins |
| `ItemGun`, `ItemMelee` | 1.5.4 | aim (`AimTargetUpdate`), turn-with-swing (`TurnWeapon`), scroll swing, release torque, recoil are all master-only since the Nov 2026 build |
| `ItemRubberDuck` | early | self-propelled |
| `ItemUpgrade` | 1.2.17 | client-side attribution race — latency advantage stole upgrades |
| `ItemCartCannonMain` (cart cannon + laser) | 1.2.21 | upright/aim pose is master-only |

## 3. Never touched at all

- **Every player action.** See section 5.
- **Enemies.** Visual smoothing only (`Smoothing.cs`), never prediction.

---

## 4. Scripted objects: host-only behaviour while held

**Rewritten 2026-11-13 (1.5.3) after re-reading the game code (Nov 5 2026 build). The
July version of this section predicted "hold a flying broom or plane and it goes dead in
your hands". That prediction was wrong; the corrected picture is below.**

A large family of items and valuables runs part of its behaviour **inside a master gate**,
so a client never runs it. The scan (master gate **and** rigidbody writes in one file):

```
ValuablePlane          WizardBroomValuable   ValuableCubeBall     ValuableArcticSnowBike
ValuableCar            ValuableStarWand      ValuableEgg          ValuableWizardStaff
ScreamDollValuable     TrafficLightValuable  BabyHeadValuable     PhoneValuable
IceSawValuable         BlenderValuable       MuseumPropMoneyHead  ValuableFlashlight
ItemLeafBlower         ItemLadder            ItemMine             ItemMineStun
ItemStaffTorque        ItemStaffVoid         ItemStaffZeroGravity ItemReviveItem
ItemOrbMagnet          ItemTracker           ItemWalkieTalkie     ItemValuableBox
ItemGrenade            ItemGrenadeExplosive  ItemHealthPack       ItemEquippable
EnemyValuableThrower
```

The scan is a list of files to read, not a list of bugs — most of these gate something
harmless. The valuables are bigger than this list: 32 classes derive from `Trap` and about
40 more `Valuable*` / `*Valuable` scripts exist.

### Where host-only behaviour can be lost — two conditions

A shadowed object is continuously passive-synced toward the host pose, so host-only motion
still *arrives*; it just isn't simulated locally. It is only lost where the mod
**withholds host data**, which happens in exactly two places:

1. **`TickHeld`** — while YOU hold it, the network does not touch it at all (NR rule).
2. **Riding cargo** — while it rides in a cart in use, it gets no sync (NR rule).

### What the self-propelled ones really do (read, not guessed)

None of the three originally "confirmed" examples propels itself in either condition:

- `ValuablePlane` — the flight forces are in `FixedUpdate` → `StateMachine`, which is **not
  master-gated**; the state arrives by `UpdateStateRPC`, so a simulating client flies it
  locally too. Line 115 only gates `ServerSideThink`, and that puts the plane in `Idle`
  (no forces) whenever anyone holds it or it sits in a cart (213–216).
- `WizardBroomValuable` — while grabbed the host switches to `States.grabbed`: zero
  gravity and low drag, **no propulsion** (193–206). In a cart it calls `TrapStop` (111).
- `ValuableCubeBall` — every bounce impulse requires `!physGrabObject.grabbed` (46/66/87).

So there is no dead plane to fix, and the dynamic "exempt while flying" pattern is not
needed for these.

### What IS host-only while held (by design since 1.2.14)

The holder does not feel these; other players see them on the host's copy:

- trap valuables that jolt or shake in the hand — `ScreamDollValuable` (37–43) and
  `IceSawValuable` (147–154) apply random impulses while grabbed. `BabyHeadValuable`,
  `PhoneValuable`, `TrafficLightValuable` and `MuseumPropMoneyHead` have host-only impulses
  of the same kind (not each checked for the held case);
- recoil and kick forces — the staffs, `ValuableStarWand`, `ValuableWizardStaff`,
  `ItemLeafBlower`;
- hold orientation asked for with `TurnXYZ` on the host: items mirror the host's rotation
  gently (`mirrorHeldRot`).

`ItemGun` and `ItemMelee` were in this group with a locally computed hold orientation
(1.1.8 / 1.2.0). The Nov 2026 build grew their host-only part far past that (see section
2) and a two-player session showed it: "they move weird and incorrect", thrown-weapon
corrections up to 1.1m. Since 1.5.4 they are not simulated at all.

Accepted trade: a held object is yours completely. If a trap valuable ever needs to be felt
by the holder, use the drone pattern (exempt only while it is active), never a blanket
exemption.

### Host Assist must stay off scripted objects (1.5.3)

Host Assist reverses the direction: the host follows the HOLDER's copy. Its `Steer`
disables the host's grab forces (`PhysGrabObject.cs:1107` skips the position pull **and**
the `TurnXYZ` orientation torque) and overwrites both velocities every tick. On a plain
object that is the whole point. On a scripted one it cancels everything in the list above
for **every** player, and a `mirrorHeldRot` item would chase its own echo (we copy the
host's rotation while the host copies ours).

Rule, in `HostAssist.HostDrivenReason`: only carts and plain physics objects are streamed.
Not streamed — any item (`ItemAttributes`), anything with a `Trap` component, and anything
carrying a script whose name contains `Valuable` (apart from the plumbing every valuable
has: `ValuableObject`, `ValuableDiscover*`, ...) or starts with `MuseumProp`. Those behave
exactly as on a host without Host Assist. The rule errs toward "host-driven": a wrong yes
only costs the assist on that object. The host applies the same check, so a 1.5.2 client
cannot make it steer one either.

**Status:** the 1.5.3 rule comes from reading the code and is untested in a lobby. With
verbose logging, each grab logs `[assist] <name>: streamed to the host` or `not streamed —
the host drives it (<script>)`; check that plain loot says streamed.

---

## 5. Player-side: everything is a host round trip

Every `RpcTarget.MasterClient` call in the player/grab code — i.e. "client asks the host and
waits":

| Call | What it gates | Predicted by us? |
|---|---|---|
| `PhysGrabObject.GrabStartedRPC` (1802) | grabbing an object | **yes** |
| `PhysGrabObject.GrabEndedRPC` (1823) | releasing / throwing | **yes** |
| `PlayerTumble.TumbleRequestRPC` (622) | **pressing Q to tumble AND to get back up** | no |
| `PlayerTumble.TumbleForceRPC` (582) | force applied to your tumble | no |
| `PlayerTumble.TumbleTorqueRPC` (601) | torque applied to your tumble | no |
| `PlayerTumble.TumbleOverrideTimeRPC` (563) | tumble duration override | no |
| `PhysGrabber.GrabClimbEndedRPC` (1920) | end of a ledge climb | no |
| `PlayerAvatar.FallingSetRPC` (1046) | falling state | no |
| `PhysGrabObject.SetPositionRPC` (1482) | forced reposition | no |
| `PlayerController.ResetPhysPusher` (326) | phys pusher reset | no |

**The tumble flow.** `PlayerController.cs:1157` (go down) and `:1146` (get up) both call
`TumbleRequest` → RPC to master → host runs `TumbleSet` (which applies the *launch* force,
~line 645) → host broadcasts `TumbleSetRPC` to All. So **both directions of Q cost a full
round trip**, and the launch upgrade is applied on the host, not locally.

**Tumble wings are split.** `ItemUpgradePlayerTumbleWingsLogic.cs:227` returns unless
master, before the lift forces (244, 250) and torque (254) — but the *visuals* run for
`IsMasterClientOrSingleplayer() || isLocal` (line 152). So on a client you **see** your
wings deploy instantly while the lift holding you up comes from the host. Classic "looks
right, feels wrong".

**Climb** is evaluated locally (`upgradeTumbleClimb` read off
`PlayerController.instance.playerAvatarScript` in `PhysGrabber` 468/509/571/2137); only the
*end* of a climb round-trips.

**Already local, no problem:** normal jump, movement, crouch, sprint.

### Decision (2026-07-25): leave player prediction alone

Deliberate, not an oversight. Mispredicting a prop makes a prop jump; mispredicting the
*player* rubber-bands their own body and camera, which feels far worse. The host can also
legitimately refuse a tumble (stun, disabled input, `tumbleOverride`), so any prediction
needs a clean rollback path. If it is ever built: start with the two Q presses behind their
own config switch, wings second.

**NOTE:** nothing about tumble is fixed today. The only tumble-related change ever shipped
was 1.2.22, which smoothed *other players'* tumbling bodies (a visual fix in `Smoothing.cs`).
Since game v0.5.0 the transform view drops every packet not sent by the host before decoding
it, so that owner-streamed data no longer exists and 1.3.0 removed the rule. Your own Q
delay was never touched.

---

## 6. Game v0.5.0 networking (what the 1.3.0 port is built on)

- `PhotonTransformView` sends ONE packed `byte[]` per object (`SemiNetPackedStream`): flags,
  teleport revision, velocity, spin, position, rotation; reduced precision unless needed.
- The client decode ignores non-host senders and drops a packet identical to the last one
  (`SemiNetPackedStream.ReceiveChanged`). `lastReceiveTimestamp` only moves when a packet is
  actually applied — that is how RevivalSync tells fresh host state from a repeat.
- Host teleports bump `teleportRevision`. The view's own `teleport` flag is ALSO set when the
  local copy is 5m+ off, which is not a teleport.
- Interest throttling: objects nobody is near (and not grabbed, not discovered valuables, not
  enemies or players) send only a 2s heartbeat. Per-object silence means nothing; host
  liveness is judged from ANY host serialization (`SerializeReadCapturePatch`).
- RevivalSync reads host state from the game's own decode, and a transpiler filters the
  decode's rigidbody writes (kinematic, velocity, sleep/wake; first-packet placement only for
  held objects) for objects it simulates.
