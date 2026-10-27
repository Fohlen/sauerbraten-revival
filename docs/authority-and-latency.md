# Authority & Latency

Why we diverge from Cube 2's client-authoritative network model, and what that
costs in latency. This is a **product-level decision** driven by the Steam
release and its leaderboard, not a purely technical one — so it is written down
explicitly rather than left implied by "server-authoritative world" in the
architecture table.

See [vision.md §7](vision.md#7-world-command-system-editor--game-modes--network)
for the command system and [editor-and-commands.md §3.2](editor-and-commands.md#32-channels--reliability)
for channels.

---

## 1. The decision

**The server is authoritative for anything with consequences.** Clients predict
for responsiveness, but the server's state is the truth.

| System | Authority | Notes |
| --- | --- | --- |
| World edits (all sources) | **Server** | already decided (editor doc §5.5); the `Sink` validates and sequences |
| Movement / position | **Server**, with client prediction + reconciliation | the hard, latency-sensitive one |
| Hitscan / combat | **Server** (client sends intent; server raycasts) | never trust a client-computed hit list |
| Cosmetic / UI / particles | Client | no consequence |

**Hard requirement of the Steam + leaderboard goal.** A public, persistent,
comparable score makes client authority a known-losing combination: a modified
client can produce output indistinguishable from legitimate play, because under
client authority the client *is* the source of truth. This is not a hypothetical
— see §2.

> **Framing.** This is not "we don't trust players." It is "the leaderboard must
> mean something." If the leaderboard were dropped or gated behind
> verified/demo-reviewed submissions, client authority would be a legitimate
> choice (§6).

---

## 2. Why not Cube 2's model (the anti-pattern, from the source)

Cube 2 is client-authoritative **throughout**, and the original source shows it
plainly:

- **Firing:** the *client* runs `raydamage`, builds the hit list, and sends it —
  `addmsg(N_SHOOT, "rci2i6iv", d, ..., from, to, hits.length(), ...hits...)`
  (`fpsgame/weapon.cpp:819`). The server (`server.cpp:3215`) unpacks the hits and
  trusts them. There is **no server-side hitscan** anywhere.
- **Positions:** `N_POS` sends delta-compressed `o`/`vel`/`falling` as *state*
  (`client.cpp:955`) — the client dictates where it is.
- **Editing:** `msgfilter` permits clients to send `N_EDIT*`, `N_REMIP`,
  `N_DAMAGE`, `N_CLIENT`, …; the only guard is a rate limit
  (`++ci->overflow >= 200`), not content validation.
- **`if(ci->local) return type;`** — the listen-server host can send anything.

Consequences: aimbot/silent-aim (report impossible hits), damage/health injection,
teleport/speed (positions are self-reported), ammo/items, plus wallhack (PVS is
shipped to clients). Cube 2's defense was **social** — trusted admins, small
community, nothing at stake. That defense does not survive a public leaderboard.

Two things made client authority *reasonable in 2004–2008* that are no longer
true: movement-as-identity wanted zero input latency, and the download-and-host
model wanted near-zero server CPU. Note the server was already cheap because it
**simulates nothing** — modern CPU headroom is not the deciding factor.

---

## 3. What "server-authoritative movement" means, concretely

The CS:GO-style model: **server simulation + client prediction/reconciliation +
interpolation + lag compensation.** Three mechanisms, often conflated:

1. **Prediction & reconciliation (your own movement).** The client simulates its
   own movement immediately on input (zero perceived latency). The server
   simulates the same inputs. The client keeps unacknowledged inputs; when the
   server acks input `N`, the client snaps to the server state and **replays**
   `N+1…now`. If prediction was right, the replay lands identically and nothing
   is seen; if it diverged, a correction (snap or smoothed).
2. **Interpolation (everyone else).** Other players are *not* predicted; render
   them in the past, interpolating between the two latest snapshots (~2 ticks
   back). Smoothness over freshness.
3. **Lag compensation (hitscan fairness).** On fire, the server **rewinds** player
   positions to roughly the shooter's view time (RTT + interp delay) and raycasts
   against that, so shots hit what the shooter saw. Bounded window; the cost is
   "died behind cover" artifacts.

**Our advantage:** this model *requires* a deterministic fixed-step movement
simulation, which is the project's central effort and is already ported and
verified ([movement-physics.md § 5](movement-physics.md#5-verification--validation-plan-a) / [ADR 0003](adr/0003-movement-verification.md)). Determinism is the expensive prerequisite,
and we have it.

---

## 4. The Sauerbraten-specific risk: movement *is* the skill

The model is technically well-matched, but our fidelity demands are higher than
CS:GO's in one respect:

- In CS:GO the skill is shooting; a small movement correction is cosmetic.
- In Cube 2 the skill is **movement itself** (strafe-jump chains, air control). A
  correction mid-chain that desyncs your air control is **gameplay-critical**, not
  invisible.
- Cube 2's movement is also **chaotic** — the friction lerp and air control
  amplify small state differences, so divergences accumulate rather than wash out.

So server-authoritative movement trades a *guaranteed correction-free* experience
(client authority) for a *correct but occasionally-corrected* one. At low ping
the corrections vanish; at high ping they can be worse than client authority,
because client authority at least keeps *your* simulation smooth.

## 5. The five correction sources (and which determinism fixes)

Corrections are not only float divergence. Enumerating them matters, because
"0% divergence" fixes exactly one:

| # | Source | Fixed by 100% determinism? | Response |
| --- | --- | --- | --- |
| 1 | **World-state mismatch** (in-flight `WorldCommand`, e.g. a remip) | **No** — timing | rare in play: game mode has no editor edits and a map-hash gate; predict against the last *acknowledged* command sequence (§7) |
| 2 | Other players' collision/push | No | tolerate (as CS:GO does) |
| 3 | Input timing / loss / jitter | No | input buffer + ack/replay |
| 4 | Non-replicated state read by movement (game-mode flag, jumppad, conveyor) | No | make the model's inputs explicit and replicated |
| 5 | Float/model divergence | **Yes** | the determinism campaign (features.md #3) |

**Pursue 0% divergence as "make corrections *explainable*," not "eliminate
corrections."** At 100%, every correction has a known cause with a designed
response; below it, a correction could be unknowable float drift. Source #1 is
the one Sauerbraten makes worse than CS:GO, because our world is mutable at
runtime (crumbling walls, live editing) where CS:GO's geometry is static.

---

## 6. The alternative, stated fairly

**Client-authoritative movement** (Cube 2's choice) wins if the leaderboard is not
load-bearing: zero corrections ever, near-zero server CPU, instant movement at any
ping, and Cube 2's exact feel for free. It is the right answer *if* competitive
integrity is handled socially (trusted servers, demo review, gated/verified
leaderboards). It is the wrong answer for an open, public, automated leaderboard.

**Server-side validation only** (client simulates; server re-simulates on
suspicion / spot-checks) is a third option. We are **not** choosing it: it is the
same arms race, and it wastes the one asset that makes server authority cheap for
us — a deterministic movement model, which is *exactly* what would make full
server-side simulation and validation tractable. Using determinism only for A3
testing and then shipping client authority would waste it.

---

## 7. The rule this forces: prediction scope (decided)

Prediction must be pinned to a **known** world. The rule:

> **Movement predicts only against the world state at the last acknowledged
> `WorldCommand` sequence.** A mutation that is in flight (not yet acked) is not
> simulated by the client's prediction for collision purposes.

This is what makes source #1 tractable, and it *requires* a central sequencer: a
client cannot pin prediction to a known world without a server-defined order.
`Remip` is the worked example — it is pass-through, so client and server each
re-run it, and if they re-run it at different times they collide against
different node granularities. Pinning prediction to the acked sequence means the
remip's effect becomes visible a few milliseconds late (unnoticed) instead of
causing a correction.

**Why this risk is small in practice** (editor-and-commands.md §11.8–11.9):

- **Game mode has no editor edits** — the map is static during play, as in CS:GO,
  so the in-flight-`WorldCommand` race essentially does not arise.
- **Runtime mutations are server-run and ordered.** A crumbling wall or race-mode
  door is executed by the server and broadcast in sequence order
  (authored by the server peer) — bounded and deterministic, not a client-originated race.
  World mutations share a single total order; no artificial `CommandSource` tag is needed.
- **A map hash gates the session.** Clients and server agree on the map before
  play; a mismatch refuses the match, eliminating the "client has geometry the
  server doesn't" class at the session level.

So the residual case is only "a scripted mid-game mutation races a player's
movement", which the acked-sequence rule turns into a few-ms-late effect. The rule
is therefore *required* — it is what makes the risk small — but **cheap to
validate**: a test that movement prediction converges across a server-authored
scripted mutation (built on the A3 trace harness), not an open-ended
investigation.

Reconciliation policy (snap / smooth-over-N) is a minor, local choice; with
immutable match maps the correction path barely fires. A "flag for review"
leaderboard path is not needed given the map-hash gate.

---

## 8. Calibration

| Regime | Expected behaviour |
| --- | --- |
| < 30 ms (LAN / same region) | prediction essentially perfect; run it and never notice |
| 30–80 ms | movement feels fine *if* prediction converges; corrections on interaction only — determinism verified with zero deviation |
| > 100 ms | CS:GO is "playable, noticeable"; for movement-as-skill it may be **worse than client authority** (a mid-strafe snap breaks the chain) |

Server load: authoritative movement makes the server run `move`/`collide` per
player at the fixed step — the workload `World/Physics/` was ported to support.
This is a real cost (a relay server that simulates nothing hosts ~100 instances on
a small VPS; an authoritative one hosts materially fewer), and it is the price of
a leaderboard that means anything.

---

## 9. Status

- **Decided:** server authority for edits, movement, and combat; Steam +
  leaderboard is the forcing function; client authority is ruled out for the
  intended product.
- **Decided:** permission is a mode/config concern (editor vs game mode), enforced
  in `Sink.MayApply`; the prediction-scope rule (§7) is required and cheap to
  validate, not an open investigation.
- **Prerequisite:** movement determinism is verified with zero deviation
  (features.md #3, Plan A complete); remaining is proving it under a prediction
  loop (§5 source #5, then #1) — **before** designing M6/M7.
- **Remaining:** a small reconciliation policy (snap / smooth) and the map-hash
  gate; both are local, not architectural.

---

## 10. Server-authoritative hitscan & lag compensation design

### 10.1 The divergence from Cube 2

In original Sauerbraten (`src/fpsgame/weapon.cpp:819` and `src/fpsgame/server.cpp:2361`),
the **client** raycasts against other players and sends a pre-computed list of
hits (`addmsg(N_SHOOT, ..., hits)`). The server trusts those hits directly,
providing zero defense against silent-aim or hit injection.

In **Sauerbraten Revival**, clients send **firing intent only**; the server
runs the authoritative raycast and collision resolution.

### 10.2 Workflow

1. **Client firing intent**:
   - The local player predicts immediate feedback: plays hudgun firing animation,
     spawns local tracer lines (`WeaponEffectsRenderer`), applies local shooter
     recoil (`Entity.Vel += KickbackVel`), and starts the cooldown timer.
   - Client sends `ShotIntentPacket`: `{ ClientTick, GunId, From, Dir, Seed }`.
2. **Server validation & lag compensation (rewind phase)**:
   - Validates shooter state (alive, cooldown satisfied, ammo available).
   - Computes the client's view time: `ClientViewTime = ServerTime - (RTT / 2) - InterpDelay`.
   - Rewinds other players' bounding cylinders (`PhysEnt`) using a ring buffer of
     recent position history (~500 ms).
   - Executes authoritative raycast using `WeaponHitscan.Fire` against the rewound
     candidate cylinders and server world octree.
3. **Application & replication**:
   - Applies damage via `PlayerState.DoDamage` and momentum transfer (`hitpush`).
   - Broadcasts `ShotResultPacket` to all clients to replicate confirmed damage,
     health deduction, and death events.

