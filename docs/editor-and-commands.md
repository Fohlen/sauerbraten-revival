# Editor & Command System

Design plan for the **editor MVP** (features.md #5) and the **command system**
that powers it, undo/redo, runtime geometry, networking, demos, and prefabs.

This document is the reasoning; the implementation status lives in
[features.md](features.md). It complements [architecture.md](architecture.md)
§2 (command system) and §4 (level format) and [rendering.md](rendering.md)
(dirty-chunk rebuilds).

> **Thesis.** In Sauerbraten every edit was a local mutation of a `cube[]`, and
> sync was a separate, ad-hoc concern (the `editinfo`/`undoblock` binary blobs in
> `octaedit.cpp`, the `sendserveredit` path, prefabs as `.obr` files). That split
> is why big edits couldn't be replicated cleanly and why prefabs/recipes were a
> second-class hack. We collapse the split: **a world mutation *is* a command, a
> command is *the* unit of edit, undo, network sync, demo recording, and prefab
> content.** One mechanism, five features. This is what makes an octree editor fit
> Godot's multiplayer stack instead of fighting it.

---

## 0. Goals & non-goals

**Goals**

1. Every octree/entity mutation is a serializable command with a deterministic
   effect and a computable inverse.
2. The command **log is replayable** — the same stream reconstructs the same
   world on any client, from any starting snapshot.
3. Works with Godot's `MultiplayerAPI` / ENet / Steam: a single ordered,
   reliable stream; server authoritative; late joiners get snapshot + log tail.
4. Undo/redo is just the command stack (inverse application), not a parallel
   mechanism.
5. Prefabs, "recipes", and copy/paste are the **same** command stream — a block of
   commands extracted from one world and applied to another.
6. The saved map format **is a versioned command history plus periodic
   snapshots** (see §7), so maps are diffable, reviewable, and merge-friendly.

**Non-goals**

- No `octaedit.cpp` plumbing, no `undoblock` binary format, no CubeScript, no
  `.obr`.
- Not byte-compatible with `.ogz` (see [architecture.md](architecture.md) §4).
- Not a fully client-side-authoritative editor; the server owns the world.

---

## 1. The core abstraction

> **Naming note.** This system is **shared**, not editor-only: the editor, game
> modes (crumbling walls, scripted platforms) and scripts all submit the *same*
> kind of `WorldCommand` through the *same* `WorldCommandSink`. The old
> `Edit*` names were a misnomer — there is no "edit command" distinct from a
> "game command", only different **sources**. Only the editor *cursor*
> (`EditCursor`) is editor-specific.

### 1.1 Command

```csharp
namespace SauerbratenRevival.World.Commands;

/// Stable numeric id — part of the wire + save format. Never reorder; append only.
public enum WorldOp : ushort
{
    // --- selection/structural (mirrors Cube2's EDIT_ enum where it maps) ---
    AddCube, RemoveCube,       // add/del leaf cube in the selection
    ExtendSelection, SetSelection, ClearSelection,
    // --- face edits ---
    SetFaceTexture,            // vslot + per-face UV carry
    SetFaceMaterial,           // empty-space material (air/water/lava/glass/clip/...)
    FlipFace, RotateFace, ReplaceFace,  // EDIT_FLIP / EDIT_ROTATE / EDIT_REPLACE
    // --- volume ops ---
    Fill,                      // sel/fill: solid fill in the selection
    Remip,                     // merge/split runs (EDIT_REMIP), recomputes cube sizes
    // --- clipboard (implemented on top of record/replay, §6) ---
    Copy, Paste, RotateSelection, FlipSelection,
    // --- entities (the same stream!) ---
    AddEntity, RemoveEntity, MoveEntity, SetEntityAttribute,
    // --- lifecycle ---
    Snapshot,                  // no-op marker; carries a full/delta world snapshot index
}

/// Who issued a command. Semantic (part of the wire/save format): it decides what
/// consumers may do with an entry, e.g. only `Editor` commands are undoable (§5.5).
public enum CommandSource : byte { Editor = 0, GameMode = 1, Script = 2 }
```

```csharp
public sealed class WorldCommand
{
    public WorldOp Op;
    public ulong Sequence;        // monotonic; total order in the log
    public int AuthorPeerId;      // who issued it (attribution, not authority)
    public CommandSource Source;  // Editor / GameMode / Script — see §5.5, §9
    public ICommandPayload Payload; // op-specific, typed; encoded by WorldCommandCodec (§5)
}
```

**Principles**

- **Deterministic**: applying a command to a given world state always yields the
  same result. No wall-clock, no RNG seeded per client, no host floats. RNG, if
  any op needs it, carries its seed in the payload.
- **One sink, many drivers, one total order.** The editor, game modes and scripts
  all go through the same sink and the same log. A single sequence is required
  because they mutate the *same* octree and races must resolve identically on
  every peer (§5.5).
- **Atomic at the *selection* granularity, not the cube**: `Fill` over a 64³
  selection is *one* command (one payload, one undo entry, one network message),
  not 262144. This is the key divergence from Cube2's per-cube `pastecube` loop
  and the reason large patches sync fine.
- **Inverse is derived, not stored**: selection-scoped ops know the old block
  (they captured it as their pre-state); the inverse is "restore that block".
  See §5.3.
- **Undo is source- and author-scoped**: undo rolls back only *your own* `Editor`
  commands. Game-mode/script commands are never undoable (§5.5).
- **The selection/cursor is client-local, not replicated.** Each client owns its
  cursor and selection state; it is **never** part of the command log or shared
  world state. The `SelectionPayload` riding on a command describes *what that
  command edited*, so peers can apply it — it is not a broadcast of anyone's
  cursor. You cannot see another player's selection (Cube2 behaves the same way).
  A coop "see each other's cursor" affordance would be a separate, ephemeral
  broadcast, not a `WorldCommand`; not planned.

### 1.2 Why selection-scoped matters (the Cube2 lesson)

Cube2's `makeundo`/`addundo` captures a `block3` (the selection's cubes) *before*
editing, and `packundo` compresses it with zlib. That is already "capture the
old block" — good. The failure modes were:

- The captured block was **not the unit of replication**; replication used
  `editinfo` + `sendserveredit` and was limited in size, so a large paste was
  local-only (`nompedit`).
- Prefabs were stored separately as `.obr` and pasted via a different path.

We fix both by making *captured old block* the payload of a `ReplaceBlock`-style
command. Then paste, prefab placement, undo, and a large fill are literally the
same message shape. See §5.3 and §6.

### 1.3 How this compares to Cube2's `N_EDIT*` protocol

Sauerbraten *does* have a command system — it is just not called that. Two
separate things carry the name "command" in the original:

- `engine/command.cpp` — the **CubeScript interpreter** (console commands, vars,
aliases). We dropped this entirely (no CubeScript).
- The network edit protocol: the `EDIT_*` op enum, the `N_EDIT*` messages, and
`edittrigger`. **This is the direct ancestor of `WorldCommand`.**

**The op enum** (`shared/iengine.h:60`):

```c
enum { EDIT_FACE=0, EDIT_TEX, EDIT_MAT, EDIT_FLIP, EDIT_COPY, EDIT_PASTE,
       EDIT_ROTATE, EDIT_REPLACE, EDIT_DELCUBE, EDIT_REMIP, EDIT_VSLOT,
       EDIT_UNDO, EDIT_REDO };
```

**The messages** (`fpsgame/game.h`) are a contiguous run:

```c
N_EDITMODE, N_EDITENT, N_EDITF, N_EDITT, N_EDITM, N_FLIP, N_COPY, N_PASTE,
N_ROTATE, N_REPLACE, N_DELCUBE, N_REMIP, N_EDITVSLOT, N_UNDO, N_REDO,
N_NEWMAP, N_GETMAP, N_SENDMAP, N_CLIPBOARD, N_EDITVAR
```

`edittrigger` (client.cpp) joins them with message-id arithmetic —
`addmsg(N_EDITF + op, ...)` — and **every edit message begins with the same 9
ints**: `sel.o.x/y/z`, `sel.s.x/y/z`, `sel.grid`, `sel.orient`, then the cursor
state `cx, cxs, cy, cys, corner`. That is exactly `selinfo` (our `Selection`,
M0) and `SelectionPayload`.

| Sauerbraten | Ours |
| --- | --- |
| `EDIT_*` enum | `WorldOp` |
| `edittrigger(sel, op, a1, a2, a3, vs)` | `WorldCommandSink.Submit(WorldCommand)` |
| `addmsg(N_EDITF + op, fmt, ...)` | `WorldCommandCodec.Encode` |
| selection prefix on every message | `SelectionPayload` (identical fields) |
| `EDIT_REMIP` carries **no arguments** (`"r"`) | `RemipPayload` (pass-through, §5.4) |
| `EDIT_UNDO`/`EDIT_REDO` are ops carrying a `packundo` blob | `ApplyInverse` wrapper; inverse captured **locally**, never sent (§5.5) |
| per-op `"ri9i6"` format strings + packed `VSlot` | one framing + `var_to_bytes` (§5.1) |
| server `msgfilter` privacy classes | server-authoritative `WorldCommandSink` |

Three deliberate divergences:

1. **Undo is not an op and its inverse is not transmitted.** Cube2 sent
   `EDIT_UNDO`/`EDIT_REDO` with a zlib `packundo` block over the wire. We capture
   the inverse locally at undo time (§5.5 Rule 4) and replicate an `ApplyInverse`
   command instead. This is what makes shared-world undo sound (your own editor
   commands only; conflicts rejected).
2. **No `N_EDITF + op` arithmetic.** The original relies on the `EDIT_*` values
   being contiguous and ordered identically to the `N_*` ids; reordering either
   silently corrupts the mapping. `WorldOp` is carried explicitly.
3. **`ReplaceBlock` unifies paste/prefab/undo/fill** (§1.2), where Cube2 split those
   across `EDIT_PASTE`, `.obr` prefabs and `packundo`.

What we **kept**, because the original got it right: a single ordered reliable
edit stream for all world mutations (coop edit), the selection prefix, and
`remip` as a zero-argument pass-through command.

---

## 2. The WorldCommandSink — one seam, many drivers **and sources**

```
                 ┌────────────────────────────────────────────┐
  Editor UI ────▶│                                            │
  Game modes ───▶│            WorldCommandSink                 │
  Scripts ──────▶│  (validates, assigns Sequence, applies)     │
                 └──────────────┬─────────────────────────────┘
                                │  every accepted command
                                ▼
                 ┌────────────────────────────────────────────┐
                 │        WorldCommandLog (append-only)        │
                 │  used by: undo, network, demo, save, tests  │
                 └───┬───────────────┬───────────────┬────────┘
                     │               │               │
             UndoStack (own cmds) NetworkSender   DemoRecorder
              submits ApplyInverse   (RPC/ENet)    (same bytes)
                     │
                     ▼  (back through the sink — never a local apply)
                 WorldCommandSink
```

- **Single entry point.** Nothing mutates the octree directly; everything goes
  `sink.Submit(cmd)`. Even game-mode "crumbling wall" schedules `RemoveCube`
  commands. This makes the world *auditable* and replayable by construction.
- **Apply is the same code path** on the issuing client, the server, and every
  peer. There is no "local edit" vs "remote edit" divergence.
- **The log is the source of truth**; the octree is a materialized view.
- **Undo is a command too** (§5.5): it is submitted back through the sink as
  `ApplyInverse`, not applied locally. See §5.5 for the full model.

Interfaces (sketch):

```csharp
public interface IWorld {                 // the mutable octree surface
    void Apply(WorldCommand cmd);          // deterministic; mutates Octa + entities
    WorldSnapshot CaptureBlock(Selection sel);
}

public interface ICommandSink {
    void Submit(WorldCommand cmd);         // local origin: validate → log → apply → replicate
    void Receive(WorldCommand cmd);        // remote origin: log → apply (no echo)
    // Undo is not a separate path: Submit(ApplyInverse(cmd)) — see §5.5.
}
```

`IWorld.Apply` is deliberately small. All the Cube2 algorithms we port
(`pastecube`, `fill`, `remip`, `replacenear`, face/texture ops) live behind it
in a ported `OctaEdit` module, one handler per `WorldOp`.

---

## 3. Fitting into Godot multiplayer

Godot's `MultiplayerAPI` (ENet now, Steam later) gives us:

- a **peer** with a `unique_id` (server typically `1`),
- `@rpc` methods with `CallLocal`, `CallRemote`, `Reliable`, `Unreliable`,
- transfer modes and channels,
- `SceneMultiplayer` with server as authority.

### 3.1 Topology

**Server-authoritative world. Clients send intents; server assigns order; server
broadcasts the canonical command.**

```
Client A:  UI action ─▶ CmdRequest(rpc, RELIABLE, ch=EDIT_REQUEST) ─▶ Server
Server:    validate(authority, perms, edit lock) ─▶ sequence ─▶ apply
           ─▶ CmdBroadcast(rpc, RELIABLE, ch=EDIT_BROADCAST) ─▶ all clients
Client *:  apply (already applied locally if A? see prediction below)
```

Why server authority even in coop edit:

- **Determinism needs a single order.** Two clients adding overlapping cubes must
  agree; the sequence number is that order.
- **Cheat/trust**: coop edit is multiplayer; a client must not be able to push
  arbitrary world state. Validation lives server-side.
- **Late join / reconnect** is trivially correct: server owns the truth.

### 3.2 Channels & reliability

There are two **kinds** of replicated thing, and they get different channels:

**(a) World mutations** — every `WorldCommand`, whatever its source (editor,
creative game mode, script). They all share the *same* ordered reliable stream,
because they mutate the same octree and must have one total order (§1.1, §5.5):

| Channel | Content | Mode |
| --- | --- | --- |
| `EDIT_REQUEST` | client → server `WorldCommand` intents (any source) | Reliable |
| `EDIT_BROADCAST` | server → all canonical sequenced commands | Reliable |
| `SNAPSHOT` | server → one client, chunked world snapshot | Reliable, chunked |

**(b) Game actions** — movement, firing, using, etc. These do **not** touch
the octree and are a *separate* system (M6/M7). They never become
`WorldCommand`s:

| Channel | Content | Mode |
| --- | --- | --- |
| `GAMEPLAY` | input/action intents (fire, use, jump) | Reliable or unreliable, per action |
| `MOVEMENT` | player physics state (features.md #8) | Unreliable (latest wins) |

**Do not put world mutations on multiple channels.** A crumbling wall is a world
mutation (`RemoveCube`, `CommandSource.GameMode`) and rides the *edit* channels
with editor edits. Splitting them would give two independent orderings; a race
between "player places a wall" and "wall crumbles" could then resolve
differently on different peers, breaking determinism. The axis that matters is
**world mutation vs. game action**, not editor vs. game mode.

World commands are **always reliable and ordered**. They are small and infrequent
compared to movement; the movement channel stays unreliable. This separation is
why we don't need Cube2's giant-patch limit: reliability is a transport property
here, and large ops are one message or a chunked reliable stream, not a
"too big to sync" special case.

### 3.3 Sending an edit (Godot API shape)

```csharp
public partial class WorldNetwork : Node
{
    [Rpc(MultiplayerApi.RpcMode.AnyPeer,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable,
         Channel = 1)]
    private void CmdRequest(byte[] cmd)
    {
        if (!Multiplayer.IsServer()) return;
        var c = WorldCommandCodec.Decode(cmd);
        if (!_authority.MayApply(Multiplayer.GetRemoteSenderId(), c)) return;
        _sink.Submit(c);                       // assigns Sequence, applies locally
        Rpc(MethodName.CmdBroadcast, WorldCommandCodec.Encode(c)); // echo to all (see note)
    }

    [Rpc(MultiplayerApi.RpcMode.Authority,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable,
         Channel = 2)]
    private void CmdBroadcast(byte[] cmd) => _sink.Receive(WorldCommandCodec.Decode(cmd));
}
```

Note on `CallLocal`: the server applies in `Submit`, so it should broadcast with
`Rpc` **excluding** itself, or mark the local apply idempotent by sequence. We
choose: `Submit` on the server does *not* apply-then-echo naively; instead the
server is the peer that owns `Sequence` assignment and broadcasts the
sequenced command to everyone including a no-op self-delivery suppressed by
sequence (`Receive` drops commands with `Sequence <= LastApplied`). This makes
reconnect/replay idempotent too.

### 3.4 Client-side prediction for the editor

Cube2 editors tried to feel local. We want the same responsiveness without
breaking server authority:

- **Optimistic apply, reconcile.** The client applies its own command
  immediately (`sink.Predict(cmd)` → provisional sequence), then observes the
  server's sequenced broadcast.
- If the server's command matches the predicted intent (same op, same payload,
  same pre-conditions), the prediction is confirmed (rebind sequence, no-op).
- If it was **rejected** or **reordered** against a conflicting command, the
  client **rolls back** to the last confirmed snapshot and replays the log tail.
  Because the log is deterministic, rollback is just "re-apply from snapshot".
- Snapshot cadence bounds rollback cost: keep a snapshot every N commands (or
  every T frames) in a ring buffer. This is the *same* snapshot machinery as
  late-join (§4).

For coop edit this rarely fires (editors rarely collide on the same cubes). For
competitive/scripted geometry, prediction can be disabled per-op (e.g. crumbling
walls are server-only, applied on broadcast).

### 3.5 Entity commands

Entities are Godot nodes, but the *definition* of an entity is data (type +
attributes + position), not a node pointer. So `AddEntity`/`RemoveEntity`/
`SetEntityAttribute` carry the data; `IWorld.Apply` maps it to a `SpawnPoint`/
`MapModel`/etc. via the existing builders. This keeps entities in the same
replicable command log as cubes — no separate "entity replication" path. It also
means a prefab can contain entities, and a demo replays entity placement.

---

## 4. Snapshots, late join, and reconnect

A command log alone can't serve a fresh client (it doesn't have the base world).
So:

- **Base map** = the saved world in our native format (features.md #2/#4).
- **Session log** = commands applied since the base map was loaded.
- A joining client receives: `base map hash` → (if mismatched, transfer the map /
  refuse) → `world snapshot` (optional; or just replay the base + log) → then
  live `EDIT_BROADCAST` commands.

Two equivalent strategies, chosen by size:

1. **Snapshot + tail** (preferred): serialize the current octree+entities
   (`WorldSnapshot`, the same encoder the save format uses), send chunked, then
   the commands after the snapshot sequence. O(world) once per join.
2. **Base + full log replay**: only cheap if the session log is short. Good for
   tests and demos.

The **save file is exactly this**: base snapshot blocks + command log (§7).

`WorldSnapshot` must be:
- deterministic (canonical octree traversal order),
- versioned,
- hashable (content hash for multiplayer parity — features.md #8, Workshop).

---

## 5. Encoding & inverse

### 5.1 Wire format

One `byte[]` per command, produced by `WorldCommandCodec`:

```
[u8 formatVersion][u64 sequence][i32 author][Variant-encoded positional Array]
```

The payload is a **positional `Godot.Collections.Array`** encoded with
`GD.VarToBytes` (and read with the object-free `GD.BytesToVar`, so untrusted
network input cannot smuggle an `Object`). Rationale (revised from the original
plan):

- **Positional arrays are canonical.** `var_to_bytes` output is byte-identical for
  the same array; only *dictionaries* are non-canonical (insertion order leaks
  into the bytes). Using arrays gives stable bytes — what parity hashing and
  byte-stable demos need — while deleting a large hand-rolled codec.
- **The leading version byte** lets the layout change without breaking old files.

**Parity hashing caveat:** even with `var_to_bytes`, treat the *semantic* content
(fields walked in a fixed order) as the hash input, not raw bytes. That is robust
under any future encoding change.

**FlatBuffers — considered, not adopted.** Zero-parse reads and a shared
C#/C++/Python schema are attractive, but FlatBuffers is *not canonical by default*
(breaks parity hashing) and adds a `flatc` toolchain. At our command sizes
(remip ≈ 20 bytes; a large block ≈ hundreds of KB) parse cost is not the
bottleneck — meshing is (~1.4 ms/chunk). If a shared tool/runtime format is ever
needed it swaps in behind `WorldCommandCodec` without touching the sink. Decision 5.1b.

### 5.2 Op payloads (examples)

- `AddCube`: `Selection` (origin, size/grid, orient) — add at the selection.
- `SetFaceTexture`: per-face orient mask, face texture indices (vslot), and the
  per-face UV/vertex data the original stores. RLE-compress when a whole
  selection shares a texture.
- `SetFaceMaterial`: material bits + face mask.
- `Fill`: selection + fill material/texture.
- `Remip`: selection only (the algorithm derives the result) — but see inverse.
- `AddEntity`: `EntityType`, position (3×f32), attr1..5 (i16), reserved.
- `Snapshot`: reference by index into the snapshot ring, not inline.

Payloads use **selection-relative** coordinates (`Selection` = origin + S/grid +
orient, mirroring Cube2's `selinfo`/`block3`) so the same command is valid
regardless of where the world is.

### 5.3 Inverse derivation (undo without storing doubles)

Two families:

1. **Reversible-by-symmetry ops** (`AddCube`↔`RemoveCube`, `AddEntity`↔
   `RemoveEntity`): inverse is an op swap. Cheap.
2. **Destructive ops** (`Fill`, `Paste`, `SetFaceTexture`, `Remip`, `Replace`):
   the command **captures the old block** as part of its payload — exactly the
   `block3` capture `makeundo` did, but now first-class and replicated.

So the log entry for a destructive edit is:

```
WorldCommand { Op = ReplaceBlock, Payload = { selection, newBlock, oldBlock } }
```

and the editor-facing op (`Fill`, `Paste`, …) is sugar that produces
`ReplaceBlock`. This is the unification that makes prefab/paste/undo one thing:

- **Undo** = apply `oldBlock`.
- **Redo** = apply `newBlock`.
- **Network** = send the whole thing (reliable; large but one message / chunked).
- **Prefab** = the `newBlock` half, stored as content.
- **Copy/paste between maps** = `ReplaceBlock` where old/new come from different
  worlds (the payload is world-agnostic — selection-relative coordinates and raw
  cube data).

> This is precisely where Cube2's prefabs fell short: it *had* `blockcopy`/
> `packblock`, but the block wasn't the replication unit. Making `ReplaceBlock`
> the universal carrier removes the "big patches can't be synced" limitation.

**Compression.** Blocks compress well (Cube2 used zlib in `packundo`). We zlib
(`System.IO.Compression` / Godot's `FileAccess` compression) the block when it
exceeds a threshold; the codec declares compression in `flags`. Same bytes in
save, network, and demo.

### 5.4 Remip and determinism

`Remip` (merge/discard children, recompute cube sizes — Cube2's `remip`) is a
**pure deterministic function** of `(octree, mipvis)`: its whole merge decision
(`faces`, `textures`, `visibleface`/`genfacevecs`/`insideface`/`occludesface`)
is integer-only, with no RNG, time or float arithmetic. Two peers given the same
octree collapse the same nodes the same way, byte-for-byte.

**Decision: `Remip` is a *pass-through* replicated op.** The wire/demo payload is
just `{selection, mipvis}`; every peer re-executes the algorithm. A 10 MB
optimization is a ~20-byte message — this fully solves the Cube2
"big patches can't sync" limitation for remip.

`mipvis` is a user VAR (`VAR(mipvis, 0, 0, 1)`) that changes the collapse
decision, so it **must** ride in the payload (or be pinned). Omitting it would
let peers with different toggles diverge.

Pass-through is sound only if every peer's octree is already byte-identical when
the command applies. That is guaranteed by the log model (base snapshot + ordered
commands), but it makes the **rest of the stream's determinism load-bearing** — a
divergence bug elsewhere is faithfully propagated by every remip. We therefore
treat command determinism as a tested invariant (see §9), the same way movement
fidelity is.

Remip's **inverse** is covered by §5.5; the capture is local (never on the wire).
This splits two concerns the unified `ReplaceBlock` model conflates:
**replication** (the function, tiny) vs **undo** (the inverse, local).

### 5.5 Undo in a shared world

Cube2 treated undo as a local inverse application. That is wrong the moment the
world is shared: the inverse is a *proposal* against state that other editors may
have changed. We pin down the model here.

**Rule 1 — You may only roll back your own *editor* changes.** The undo/redo
stack holds the commands *this* client issued **with `CommandSource.Editor`**, and
nothing else. Two exclusions, both by construction:

- another peer's commands (`AuthorPeerId`, §1.1), and
- **game-mode and script commands** (`CommandSource.GameMode`/`Script`) — a
  crumbling wall or a scripted platform is never undoable, even if this client's
  server authored it.

So undo filters the log by *both* `AuthorPeerId == me` and `Source == Editor`.

**Rule 2 — Undo is an ordinary command, server-sequenced.** Ctrl-Z does not apply
an inverse locally. It submits an `ApplyInverse` command through the same
`WorldCommandSink` as any edit, and it goes through the same authority,
sequencing, and validation path. Undo therefore inherits prediction/rollback
(§3.4) for free, and cannot silently diverge from the shared world.

**Rule 3 — First writer wins; conflicts are rejected, never merged.** If someone
else has changed the cells an inverse needs to restore, the command's
pre-condition no longer holds and the **server rejects it**. The editor surfaces
"can't undo — the region was changed." We do **not** attempt OT/CRDT-style merging
of concurrent cube edits; a cube editor is not a collaborative document. Silent
corruption is the failure mode to avoid, and rejection is honest.

A rejected undo is **discarded from the stack** — it is not retried and does not
block the rest of the history. This is safe because undo is always a *conscious*
action: the user pressed a button, so telling them "sorry, this can't be undone,
the region changed" is a sufficient and honest outcome. (Undo is never applied
implicitly, so a discarded entry can never surprise anyone.)

**Rule 4 — Capture the inverse at undo time, from the current world.** Rather
than storing a region capture when an edit is applied (which can go stale), the
inverse payload is produced *when the user presses undo*, from the state being
undone. This sidesteps the "captured at apply time, stale by undo time" problem
entirely: the pre-condition is always evaluated against the world you are undoing
from. For derivation-only ops (e.g. `Remip`) there is no stored payload at all —
the inverse is recomputed/re-executed.

The consequences of Rules 3–4 for the concrete scenario are worked through in
§11.6 (concurrent remip + undo).

---

## 6. Prefabs & recipes as commands

With `ReplaceBlock` as the universal carrier:

- **Prefab** = a named `newBlock` (+ its entity commands), stored as a `Resource`
  (`.tres`/`.res`) — *not* `.obr`. It is content: shipped, Workshop-uploaded,
  hash-verified like any other asset.
- **Recipe** = a *parameterized* sequence of commands (a small data script) that
  stamps geometry/entities into a region: e.g. "pillar(height, material)" emits
  N `AddCube`/`ReplaceBlock`s. Recipes are a `Resource` with parameters and a
  `Generate(Selection) -> IEnumerable<WorldCommand>` method (data-driven, no
  CubeScript).
- **Copy/paste across maps** = extract `ReplaceBlock` from source world, apply to
  target. Identical code path.
- **Rotation/flip of a block** = a command that rewrites the block's coordinates
  and edge/face data (port of `rotateblock`/`flipblock`). Because orientation is
  part of the command, a prefab placed with a rotation is still one command.

Because a prefab is just commands, placing it is:
1. one `ReplaceBlock` (+ entity commands) on the network,
2. one undo entry,
3. one demo record entry,
4. one Workshop asset.

This is the "awesome²" the design buys: the editor's most complex feature is
mechanically identical to a cube edit.

---

## 7. Map format: versioned command history

The native format (architecture.md §4) becomes:

```
map/
├── Level.tscn            # scene: mode config, environment; REFS the data below
├── world.res             # base octree snapshot (canonical, versioned)
├── history.res           # appended WorldCommands since world.res (optional)
└── materials/            # material manifest
```

The map is a **directory, not a single file**. That is deliberate — it matches
Godot's external-resource idiom, it is diffable, and it packages cleanly into the
`.zip`/`.tar` archives Workshop expects (§7.4).

### 7.1 Data lives outside the scene (Godot external-resource idiom)

`Level.tscn` holds **references** to the data, not the data itself:

```
Level.tscn
  ├─ World : MapWorld
  │    world   = ExtResource("res://maps/church51/world.res")
  │    history = ExtResource("res://maps/church51/history.res")
  ├─ Environment : WorldEnvironment
  └─ GameMode : Node                 # mode config, rules
```

Godot scenes store external resources by path and only inline them if the path is
cleared. So editing the octree rewrites `world.res`; **`Level.tscn` never
changes** as a result of a geometry or entity edit. The scene stays small and
stable, and the #3 importer can write `world.res` + `Level.tscn` independently —
exactly the split [ogz-import.md](ogz-import.md) already plans.

### 7.2 Entities are runtime projections, not scene data

Entity nodes are **not** serialized into `Level.tscn`. `IWorld.Apply` materializes
them at load via the existing builders, with `Node.Owner == null`, so a
`PackedScene` never saves them. This mirrors the render chunks: `MapWorld` builds
`MeshInstance3D` chunks as children from the octree, not as hand-authored scene
data. Entities follow the same relationship to the command log that chunks follow
to the octree — they are a **view**, rebuilt deterministically.

Consequence: **hand-editing entities happens through the editor/command path,
never the Godot inspector.** `Level.tscn` holds only scene-level concerns
(environment, mode config, editor camera). This is the price of unified
replication, and it is worth it — the same command stream that places an entity
is what replicates, undoes, and saves it.

### 7.3 Rules

- **Snapshots are periodic.** On save, we may emit the current world as a fresh
  `world.res` and truncate the history (compaction), exactly like the snapshot
  ring in §3.4.
- **Versioned maps.** `world.res` carries a format `version`; each command
  carries `WorldOp` (append-only enum). A newer file opened by an older build can
  either (a) refuse unknown ops, or (b) — if `history.res` is present — open the
  older snapshot and skip the unknown tail. **Maps are versioned because they are
  a command history.**
- **Diffable/reviewable.** A text encoding of the history (or a `.tres` sidecar)
  lets a map change be reviewed like a code change. This is a real product feature
  for a community that iterates on maps. (Binary `.res` for load speed; optional
  text for VCS — the same split Godot itself uses for `.res` vs `.tres`.)
- **Demo/replay = same bytes.** A demo is `base snapshot ref + command log +
  movement stream`. Because edit bytes are already canonical, demos and saves
  share the codec (features.md #9). Visual A/B against the original becomes
  "play the recorded stream".

### 7.4 Where map data is stored (Godot storage tiers)

Godot has three storage tiers, and the editor uses two of them:

| Tier | Path | Writable at runtime? | Used for |
| --- | --- | --- | --- |
| Project assets | `res://` | **No** (read-only once exported) | shipped maps, materials, models |
| User data | `user://` | **Yes** (guaranteed writable) | maps being *created/edited*, session saves, downloaded UGC |
| Editor data | OS editor dir | editor only | — (not used) |

- **Creating/editing a map** writes to `user://maps/<id>/` — `res://` is
  read-only at runtime, so live edits cannot round-trip there.
- **Shipping/subbing a map** is a `res://` (packed) resource, or a mounted
  `.pck`/`.zip` via `ProjectSettings.load_resource_pack` ([architecture.md §7](architecture.md#7-modding--workshop)).
- **Writing** uses `ResourceSaver.Save(res, "user://.../world.res",
  ResourceSaver.SaverFlags.Compress)`; **reading** uses `ResourceLoader.Load`.
- For a shipping game set `application/config/use_custom_user_dir = true` so
  saves live under the game's own name rather than Godot's shared data dir.

**Hashing & distributing:** the hash is computed over the map **archive**, not
over individual files. Workshop (and our parity checks in architecture.md §7)
expects `.zip`/`.tar`, so the content hash is the archive hash — one value that
covers `Level.tscn` + `world.res` + `history.res` + `materials/` together. This
is why the directory layout above is safe: the unit of distribution, hashing, and
parity **is** the archive.

```
map archive (map.zip / map.tar)
├── Level.tscn
├── world.res
├── history.res
└── materials/
    └── ...
```

---

## 8. Applying to the octree: dirty chunks & budget

`IWorld.Apply` mutates the octree, then notifies the renderer. This ties into the
already-planned dirty-chunk system (rendering.md §Chunking):

1. Command reports its affected `Aabb` (selection expanded by boundary — Cube2's
   `readychanges` expands by 1 to catch merges/t-joints).
2. `ChunkedWorldMesh` marks overlapping chunk ids dirty (bitmask/set).
3. Per-frame budget re-meshes up to N dirty chunks (`OctaMesher.BuildChunked`
   refactored to rebuild one chunk).
4. Collision reads the live octree immediately — no shape rebuild (rendering.md
   §Collision).

Determinism caveat: meshing must not affect world state. It doesn't — the octree
is the truth; meshes are derived. This keeps rollback cheap (re-mesh only dirty
chunks after replay).

**TODO prerequisite:** incremental chunk rebuild API (currently `BuildChunked`
is all-at-once). This is a blocker for the editor and should land first.

---

## 9. Testing strategy (fidelity is the product)

Commands make the editor **testable**, which Cube2's editor never was:

- **Round-trip**: apply command → encode → decode → apply on a copy → worlds
  equal (octree checksum, like `OgzGeometry`). 
- **Inverse**: apply cmd, apply inverse → checksum equals original.
- **Replay determinism**: apply a random command *sequence* to the same base and
  compare checksums across K replicas (this is the editor's A4).
- **Replication**: run a headless server + two headless clients in-process
  (Godot `MultiplayerPeer` loopback / `OfflineMultiplayerPeer`), apply a log,
  assert convergence.
- **Prefab/recipe**: stamp a prefab → compare against the same block copied
  directly.
- **Golden maps**: like `test/OgzReaderTest.cs`, a fixture command log + expected
  checksum.

The existing `OctaGeometry` canonical checksum is the assertion primitive. We add
a `WorldSnapshot` checksum for entity streams too.

**Ground truth over reasoning.** For the octree primitives, the edge macros
(`cubeedge`/`edgeset`/`octaindex`/`octastep`) are easy to get subtly wrong by
hand. `tools/octaprobe/` links a small C++ driver against the original `octa.cpp`
and dumps exact output (`lookupcube`, `subdividecube` on a deformed cube,
`blockcube`). Ports are asserted against those bytes, not derived by argument —
the same discipline the A1 movement harness uses. The octaprobe immediately
caught a missing `validatec` pass in `SubdivideCube`.

---

## 10. Implementation order (milestones)

Ordered so each step is independently verifiable and unblocks the next.

**M0 — Selection model (`selinfo` port).** `Selection` struct: origin, size, grid,
orient, corner tracking, plus `blockcopy`/`block` capture. Pure data, heavily
unit-tested. *No dependencies.*

**M1 — `IWorld.Apply` + op handlers (single-player).** Port `pastecube`, fill,
face texture/material, add/remove, remip against `Octa.cs`. Test via
round-trip/inverse checksums. This is the editor's engine, no UI/network yet.

> **Progress: M1 complete.** M1a (structural primitives), M1b
> (lookups/mapping/subdivision), M1c (block capture, transparent paste, delete,
> fill), and M1d (face/texture/material/flip/rotate, `forcemip`, `remip`) are
> **done**, each asserted against the original engine via `tools/octaprobe`
> (`test/OctaEditTest.cs`, `OctaLookupTest.cs`, `OctaOpsTest.cs`, `OctaFaceTest.cs`,
> `RemipTest.cs`). Next: **M2** (incremental meshing), then **M3** (command log).

**M2 — Incremental meshing.** Refactor `OctaMesher.BuildChunked` into per-chunk
rebuild + dirty set + per-frame budget. Bakes in the rendering side early so
edits are visible while we build the rest. *Prereq for any live editing.*

**M3 — Command log + undo/redo (done).** `WorldOp`/`WorldCommand`/payloads,
`WorldCommandCodec` (`var_to_bytes` over positional arrays, §5.1), `IWorld`/`OctaWorld`
apply seam, `WorldCommandLog` (sequence + own-commands-only undo stack),
`WorldCommandSink` (submit/receive/undo/redo, §5.5 rules). Offline; asserted by
`test/CommandSystemTest.cs`. **Note:** undo/redo tests compare a
subdivision-invariant *content* snapshot, not the `OgzGeometry` structural
checksum — `LookupCube` subdivides as a side effect, so equivalent octrees can
hash differently (§9).

**M4 — Editor UI (Godot).** Core editing mechanics first, then GUI:

- **M4a — cursor cast**: eye ray → `(lu, gridsize, orient, cur, cor, corner)`,
  `editmoveplane`/`rayboxintersect`/`normalizelookupcube` (`World/Editor/`).
  **Done** — `CursorGeometry` + `EditCursor`, asserted against the engine by
  `test/EditCursorTest.cs`. (The world ray cast that supplies `rayDist` is still
  to be wired from the octree.)
- **M4b — grid/orient/selection**: `gridpower`, `reorient`, drag-select,
  `selextend`, `cancelsel`, selection moves; client-local state (see §1.1), never
  in the log. **Done** — `EditCursor` commands, asserted by `test/EditCursorTest.cs`.
- **M4c — wheel dispatch + `editface` modes 0/2**: `linkedpush`/`pushedge`,
  cube-face and cube-corner push/pull. **Done** — `OctaEdit.EditFace`/`PushEdge`/
  `LinkedPush`, asserted against the engine by `test/OctaFaceTest.cs`. Mode 1
  (fill) was already `OctaEdit.Fill`.
- **M4d — the selection box + input**: render the white rectangle + grey grid +
  red origin marker; wire the camera ray → `EditCursor.Update`; wheel/trackpad
  dispatch. **Done** — `OctaRay` (leaf-stepping ray cast), `EditorController`,
  `EmptyMap`/`scenes/empty_map.tscn`. `test/OctaRayTest.cs`, `test/EditorInputTest.cs`,
  `test/DragSelectTest.cs`.

  **Input binds wired** (Cube2 `editbind`): `MOUSE1` box-select (editdrag),
  `MOUSE2` extend (editextend), wheel/trackpad scroll = fill/empty, and the
  `delta_use` **modifier namespace** — hold `G` grid, `F` face push/pull,
  `Q` corner push/pull, `R` rotate; `X` flip, `SPACE` cancel, `DELETE` delcube,
  `KP4-9` nudge selection. **macOS note:** trackpad/Magic-Mouse scrolling arrives
  as `InputEventPanGesture`, not `WheelUp/Down` — both are handled.

  **Todo (engine work):** replace `OctaRay.RayCube` with a port of
  `physics.cpp:raycube` (hierarchical descent + deformed-cube clipping planes).
- **M4e — GUI**: texture/material pickers, entity placement, keybinds via
  `InputMap`. Features.md #5 checklist.

The wheel is bound like Cube2: `InputMap` actions for wheel up/down plus a
modifier namespace selecting grid / face / corner / rotate (§ see the
`delta_do` note below).

**M5 — Save/load in native format.** `WorldSnapshot` encoder/decoder; `world.res`
+ `history.res`; version field; compaction; archive packaging + content hash
(§7.4). Round-trip tests. *Unblocks #3 importer too.*

**M6 — Networking.** `WorldNetwork` RPC nodes, channels, authority, sequence,
snapshot transfer, late join. Headless multi-client tests. *Unblocks coop edit.*

**M7 — Prediction/rollback.** Snapshot ring, optimistic apply, reconcile.
*Can ship coop edit without this first; optimizes feel.*

**M8 — Prefabs, recipes, cross-map paste.** `ReplaceBlock` content resources,
rotation, recipe API, Workshop packaging hook. *The payoff layer.*

**M9 — Runtime geometry for game modes.** Modes submit `WorldCommand`s with
`CommandSource.GameMode` through the same sink (crumbling walls, scripted
platforms; architecture.md §1). They are never undoable and ride the edit
channels. *Shares everything above; no new mechanism.*

Milestones M0–M3 are the real work and are all testable without a window. M4 is
the visible editor. M5+M6 are the multipliers. M8 is the differentiator.

---

## 11. Open questions / decisions

1. **Snapshot cadence for rollback**: every N commands, every T ms, or every
   frame? Trade memory vs rollback latency. Suggest: ring of 32 snapshots,
   promote-to-full when a block edit exceeds a size threshold.
2. ~~**Entity authority detail**~~ — **decided (§7.2):** entities are runtime
   projections (`Owner == null`), never serialized into `Level.tscn`. Hand-editing
   entities is editor/command-path only.
3. **Text vs binary history log**: text is diffable/reviewable but larger and
   slower. Suggest: binary `.res` for load, generate a text sidecar for VCS
   optionally (like Godot's `.tres` for `.res`).
4. **Prediction policy by source, not op**: editor submissions can be optimistic
   (local stamp, reconcile); game-mode/script geometry is server-authored and
   applied on broadcast. This is naturally a property of `CommandSource`, so we
   likely do not need a per-`WorldOp` flag.
5. ~~**Undo across remip**~~ — **decided (§5.4, §5.5):** `Remip` is a
   pass-through replicated op (payload `{selection, mipvis}`, deterministic
   integer re-execution). Undo is a normal `ApplyInverse` command, capturing the
   inverse at undo time; it is never transmitted and needs no special case.
6. **Block compression**: zlib level and threshold; must be identical across
   platforms for the parity hash (zlib is, but pin the library/level). Note this
   is *inner* block compression inside the codec, separate from the *outer* map
   archive (§7.4), whose hash is the parity unit.
7. **Float determinism**: positions are f32 on the wire; confirm no accumulation
   of arithmetic in apply paths (only integer octree math + exact f32 copies).
   Pass-through `Remip` (§5.4) makes this load-bearing: a float in any apply path
   would diverge peers. `visibleface` and its subtree are verified integer-only.
8. **Permission/authority model**: who may edit (all coop clients? server
   config?), and how it interacts with the edit lock and game modes.
9. **Prediction scope (M6/M7)**: movement must predict only against the world at
   the last **acknowledged** `WorldCommand` sequence, or an in-flight mutation
   (notably a pass-through `Remip`) makes client and server collide against
   different state. The rule is stated in
   [authority-and-latency.md §7](authority-and-latency.md#7-the-rule-this-forces-prediction-scope-open);
   the reconciliation policy (snap / smooth / flag-for-review) is undecided.
10. ~~**Deformed-cube subdivision fidelity**~~ — **closed in M1b**: `SubdivideCube`
   now resamples deformed edges (`getcubevector`/`midedge`/`crosscenter`) and runs
   the `validatec` pass, matching the original's exact child edge bytes. Verified
   by `tools/octaprobe` (a C++ driver linked against the original `octa.cpp`) and
   asserted in `test/OctaLookupTest.cs`. Note the original's `size > 0x1000`
   auto-subdivide branch in `validatec` is not ported (never fires on the editor
   path).

### 11.6 Worked example: concurrent remip + undo

Two editors, A and B, on a shared server. This is the scenario that motivated
Rules 1–4 (§5.5).

```
S0
A: E_A1 (adds a wall)              -> S1     A's stack: [E_A1]
B: E_B1 (textures an adjacent cell) -> S2    B's stack: [E_B1]
A: Remip(region covering both)     -> S3     (pass-through; B re-executes it)
A: Ctrl-Z  (wants to undo E_A1)
```

What happens, per the rules:

- **Rule 1:** A's Ctrl-Z is about `E_A1` (A's own `Editor` command). It is
  permitted in principle; B's `E_B1` is not on A's stack and cannot be undone by
  A. Nor could a `GameMode` command (e.g. a crumbling wall) ever be undone — it
  is not `CommandSource.Editor`.
- **Rule 4:** A's inverse is computed *now*, from `S3` (the current world), not
  from a block captured back at `S1`. So there is no stale payload.
- **Rule 2:** A submits `ApplyInverse(E_A1)` through the sink; the server
  sequences and applies it like any edit.
- **Rule 3:** the inverse removes A's wall. If B's `E_B1` touched a cell inside
  A's wall, the **first writer (A) wins** — B's texture was applied to a cube A is
  now removing, so it is simply gone with the wall. If instead A's inverse needs
  to restore a cell B has since changed in a way that conflicts, the server
  **rejects** the undo, A is told the region changed, and the entry is **discarded
  from A's stack** (Rule 3). A is free to carry on undoing further back.

Note what remip does **not** do here: it is not a barrier, and it loses no
history. A's undo stack still contains `E_A1` for the whole time. The remip
changed node *granularity*, not the logical edit history; pasting A's inverse
re-subdivides whatever region it touches (`lookupcube` with a positive size),
producing a valid — if momentarily less-merged — octree. The next remip
re-optimises it.

The invariants this relies on:

1. **Deterministic re-execution** (§5.4) — B's remip lands where A's did.
2. **Edit-time capture, not apply-time** (§5.5 Rule 4) — the inverse is never
   stale.
3. **Rejection over merging** (§5.5 Rule 3) — no silent corruption.

---

## 12. File layout (planned)

```
World/Commands/
├── WorldOp.cs              # append-only op enum + CommandSource
├── WorldCommand.cs         # command + typed payloads
├── WorldCommandCodec.cs    # canonical wire/save/demo encoding
├── Selection.cs            # selinfo / block3 port (done, M0)
├── WorldCommandSink.cs     # submit/receive/apply, sequence assignment
├── WorldCommandLog.cs      # append-only log + own-editor-commands undo stack
├── IWorld.cs               # Apply + Capture seam (OctaWorld)
└── Ops/                   # one handler per WorldOp (ported algorithms)
    ├── AddRemoveCube.cs
    ├── FaceOps.cs         # texture/material/flip/rotate/replace
    ├── Fill.cs
    ├── Remip.cs
    ├── EntityOps.cs
    └── ReplaceBlock.cs    # the universal carrier (paste/prefab/undo/fill)
World/OctaEdit.cs          # structural primitives + ported edit algorithms (M1)
World/Net/
├── WorldNetwork.cs        # RPC nodes, channels, authority
└── WorldSnapshot.cs       # canonical snapshot encode/decode + hash
World/Editor/
├── EditCursor.cs          # editmoveplane / rendereditcursor port
├── EditorController.cs    # UI actions → commands
└── EditorUi.cs            # Godot UI: selection, texture/material/entity pickers
World/Meshing/
└── ChunkedWorldMesh.cs    # + dirty set + per-chunk rebuild (M2)
```

---

## 13. Summary

- **One mechanism**: mutation = command = network = demo = prefab. Undo is the
  *same* mechanism again (`ApplyInverse`), not a parallel one.
- **Undo in a shared world is explicit** (§5.5): you roll back only your own
  changes; undo is a server-sequenced command; first writer wins and conflicts
  are rejected, never merged; the inverse is captured at undo time, never stale.
- **Derivation-only ops are pass-through** (§5.4): `Remip` replicates as
  `{selection, mipvis}` and every peer re-executes it, so optimisation patches are
  tiny — the "big patches can't sync" limit is gone.
- **Selection-scoped, block-capturing commands** fix Cube2's bulk-edit/prefab
  limitation.
- **Server authority + reliable ordered edit channel**, unreliable movement
  channel; snapshots handle late join and rollback.
- **Versioned maps are command histories** — diffable, replayable, and the same
  bytes as demos.
- **The map is a directory of external resources**, referenced by `Level.tscn`;
  entities are runtime projections, not scene data. The unit of distribution,
  hashing, and multiplayer parity is the **archive** (`.zip`/`.tar`) — which is
  exactly what Workshop wants.
- **Prefabs/recipes are just commands**, so the editor's best feature is the
  cheapest to build on this foundation.
- **All of M0–M3 is testable headless** with the existing octree checksum
  primitive; the editor UI and networking then have almost no new mechanism.

This is the plan for making the editor "awesome²": not just a cube editor, but a
general, replicable, versioned world-mutation system that the editor happens to
be the first client of.
