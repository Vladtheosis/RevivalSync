# Physics coverage map

What RevivalSync predicts, what it deliberately hands back to the game, and what it has
never touched. Compiled by sweeping every networked physics class in REPO 0.4.4.3
(Assembly-CSharp) against the mod's actual behaviour.

Read this before adding any new object to the simulation, and before assuming a
"desync" report is a sync bug — several of the entries below are *by design*.

---

## 1. Predicted locally (the mod owns these)

Props and valuables, loot, **carts**, **doors/cabinets** (hinges), weapons, gadgets,
grenades, death heads. Anything with a `PhysGrabObject` + `PhotonTransformView` that is
not on the exemption list in `SimManager.CanSimulate`.

## 2. Deliberately handed back to vanilla

| Object | Since | Why |
|---|---|---|
| `ItemVehicle` | early | drives itself; host-only |
| `ItemDrone` (while flying / magnet-carrying) | 1.2.8 | dynamic exemption — a *carried* drone is still predicted |
| `ItemRubberDuck` | early | self-propelled |
| `ItemUpgrade` | 1.2.17 | client-side attribution race — latency advantage stole upgrades |
| `ItemCartCannonMain` (cart cannon + laser) | 1.2.21 | upright/aim pose is master-only |

## 3. Never touched at all

- **Every player action.** See section 5.
- **Enemies.** Visual smoothing only (`Smoothing.cs`), never prediction.

---

## 4. THE LATENT CLASS: self-propelled valuables with host-only motion

This is the cart-cannon bug class, unresolved. A large family of items applies its own
motion forces **inside a master gate**, so a client never runs them.

Confirmed by reading the code:

- `ValuablePlane.cs:115` — `if (!SemiFunc.IsMasterClientOrSingleplayer()) return;` then all
  flight forces (324, 355–356, 361, 391, 396)
- `WizardBroomValuable.cs:80` — gate, then forces at 141, 150, 157–158, 173, 178
- `ValuableCubeBall.cs:43/63/84` — gates, then bounce impulses at 49–50, 69–70, 90

Full candidate list (master gate **and** rigidbody writes in the same file):

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

### When it actually breaks — only two conditions

A shadowed object is continuously passive-synced toward the host pose, so host-only motion
still *arrives*; it just isn't simulated locally. The failure needs the mod to be
**withholding host data**, which happens in exactly two places:

1. **`TickHeld`** — while YOU hold it, the network does not touch it at all (NR rule).
2. **Riding cargo** — while it rides in a cart in use, it gets no sync (NR rule).

Predicted symptom: *hold a flying broom or plane and it goes dead in your hands while
everyone else sees it fly* — the exact fingerprint of the limp cart cannon.

`ItemGun` / `ItemMelee` appear in the scan but are **already solved differently**: their
hold orientation is computed locally from their own tuning fields (1.1.8 / 1.2.0), so they
do not need the host's version.

### Recommended fix pattern (if confirmed)

**Do not blanket-exempt.** Use the drone pattern: exempt dynamically only while the item is
actually doing its thing (`ItemToggle.toggleState`, flight active, etc.), so carrying an
inert broom still feels instant. Blanket exemption would make every one of these feel
host-laggy in hand for no benefit.

**Status: latent, unconfirmed.** No player has reported a dead plane. Test before fixing.

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
is 1.2.22, which smooths *other players'* tumbling bodies (a visual fix in `Smoothing.cs`).
Your own Q delay is untouched.
