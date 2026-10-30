# Editor & Command System

Design plan for the **editor MVP** (features.md #4) and the **command system**
that powers it, undo/redo, runtime geometry, networking, demos, and prefabs.

This document is the reasoning; the implementation status lives in
[features.md](features.md). It complements [vision.md](vision.md)
§7 (command system, [ADR 0008](adr/0008-command-logic.md)) and §9 (level format, [ADR 0009](adr/0009-reference-based-maps.md)) and [ADR 0007](adr/0007-world-rendering.md)
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
- Not byte-compatible with `.ogz` (see [vision.md](vision.md) §9).
- Not a fully client-side-authoritative editor; the server owns the world.

---

## 1. The core abstraction

> **Naming note.** This system is **shared**, not editor-only: the editor, game
> modes (crumbling walls, scripted platforms) and scripts all submit the *same*
> kind of `Command` through the *same* `CommandSink`. The old
> `Edit*` names were a misnomer — there is no "edit command" distinct from a
> "game command", only different **sources**. Only the editor *cursor*
> (`EditCursor`) is editor-specific.

### 1.1 Command & WorldOp (ADR 0008)

World mutations are governed by the unified FlatSharp schema ([ADR 0008](adr/0008-command-logic.md)):

```csharp
namespace SauerbratenRevival.Format;

/// FlatSharp-backed stable numeric id — part of schema.fbs and wire protocol (ADR 0008). Never reorder; append only.
public enum WorldOp : byte
{
    None = 0,
    DeleteCube = 1,            // mpdelcube
    EditFace = 2,              // mpeditface: mode 0 face push/pull, 1 fill/empty, 2 corner
    SetFaceTexture = 3,        // edittexcube
    ReplaceTexture = 4,        // replacetexcube (per-selection or whole world)
    SetFaceMaterial = 5,       // mpeditmat
    FlipCube = 6,              // mpflip (selected cubes, axis d)
    RotateCube = 7,            // mprotate (selected cubes, axis d, direction cw)
    Remip = 20,                // EDIT_REMIP (merges runs, deterministic)
    NewMap = 30,               // emptymap: fresh world
    AddEntity = 40,            // Add dynamic map entity
    RemoveEntity = 41,         // Remove dynamic map entity
    EditEntity = 42,           // Modify entity attributes or transform
    SetEnvironment = 43,       // Sun yaw/pitch/color, ambient, fog, skybox
    ReplaceBlock = 50          // Universal block paste / undo restore
}

// Single source of truth across memory, network, undo/redo, and storage (ADR 0008)
public partial class Command
{
    public ulong Sequence { get; set; }     // monotonic sequence in the total order
    public int PeerId { get; set; }         // author peer id (attribution & undo ownership)
    public uint Timestamp { get; set; }
    public WorldOp Op { get; set; }
    public CommandPayload? Payload { get; set; }
}
```

CommandSource has been eliminated: authority is governed by network peer ID / session,
and undo/redo history is captured directly by the local `EditorController` rather
than tagging low-level command structs.

**Principles**

- **Deterministic**: applying a command to a given world state always yields the
  same result. No wall-clock, no RNG seeded per client, no host floats. RNG, if
  any op needs it, carries its seed in the payload.
- **One sink, many drivers, one total order.** The editor, game modes and scripts
  all go through the same sink and the same log. A single sequence is required
  because they mutate the *same* octree and races must resolve identically on
  every peer (§5.5).
- **Copy is client-local, paste is a command.** A copy only reads the octree into a
  local buffer, so it is not a `WorldCommand`; a paste is `ReplaceBlock`. This is why
  the op list has no `Copy`/`Paste`.
- **Atomic at the *selection* granularity, not the cube**: a fill over a 64³
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
old block" — good. (We adopt the capture, not the compression — see §11.6.) The
failure modes were:

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
| `edittrigger(sel, op, a1, a2, a3, vs)` | `CommandSink.Submit(Command)` |
| `addmsg(N_EDITF + op, fmt, ...)` | `CommandCodec.Encode` |
| selection prefix on every message | `SelectionPayload` (identical fields) |
| `EDIT_REMIP` carries **no arguments** (`"r"`) | `RemipPayload` (pass-through, §5.4) |
| `EDIT_UNDO`/`EDIT_REDO` are ops carrying a `packundo` blob | `ApplyInverse` wrapper; inverse captured **locally**, never sent (§5.5) |
| per-op `"ri9i6"` format strings + packed `VSlot` | one framing + `var_to_bytes` (§5.1) |
| server `msgfilter` privacy classes | server-authoritative `CommandSink` |

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

## 2. The CommandSink — one seam, many drivers **and sources**

```
                 ┌────────────────────────────────────────────┐
  Editor UI ────▶│                                            │
  Game modes ───▶│               CommandSink                  │
  Scripts ──────▶│  (validates, assigns Sequence, applies)    │
                 └──────────────┬─────────────────────────────┘
                                │  every accepted command
                                ▼
                 ┌────────────────────────────────────────────┐
                 │          CommandLog (append-only)          │
                 │  used by: undo, network, demo, save, tests │
                 └───┬───────────────┬───────────────┬────────┘
                     │               │               │
             UndoStack (own cmds) NetworkSender   DemoRecorder
              submits ApplyInverse   (RPC/ENet)    (same bytes)
                     │
                     ▼  (back through the sink — never a local apply)
                 CommandSink
```

- **Single entry point.** Nothing mutates the octree directly; everything goes
  `sink.Submit(cmd)`. Even game-mode "crumbling wall" schedules `DeleteCube`
  commands. This makes the world *auditable* and replayable by construction.
- **Apply is the same code path** on the issuing client, the server, and every
  peer. There is no "local edit" vs "remote edit" divergence.
- **The log is the source of truth**; the octree is a materialized view.
- **Undo is a command too** (§5.5): it is submitted back through the sink as
  `ApplyInverse`, not applied locally. See §5.5 for the full model.

Interfaces (sketch):

```csharp
public interface IWorld {                 // the mutable octree + entity surface
    Octa Octa { get; }
    List<MapEntity> Entities { get; }
    void Apply(Command cmd);              // deterministic; mutates Octa + Entities
    OctaEdit.CapturedBlock Capture(Block3 region, int rgrid);
}

public interface ICommandSink {
    void Submit(Command cmd);             // local origin: validate → log → apply → replicate
    void Receive(Command cmd);            // remote origin: log → apply (no echo)
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
mutation (`RemoveCube`) and rides the *edit* channels
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

**FlatBuffers via FlatSharp — adopted ([ADR 0008](adr/0008-command-logic.md) & [ADR 0009](adr/0009-reference-based-maps.md)).** Zero-copy reads directly
from byte spans, unified networking, recorded streams, and keyframed map packages
compiled in-process via C# NuGet (`FlatSharp.Compiler`) without requiring external
`flatc` CLI toolchains or JSON conversion pipelines. Parity hashing hashes the
canonical semantic keyframes and base map SHA-256 ([ADR 0009](adr/0009-reference-based-maps.md)). Decision 5.1b.

### 5.2 Op payloads (examples)

- `AddCube`: `Selection` (origin, size/grid, orient) — add at the selection.
- `SetFaceTexture`: per-face orient mask, face texture indices (vslot), and the
  per-face UV/vertex data the original stores. RLE-compress when a whole
  selection shares a texture.
- `SetFaceMaterial`: material bits + face mask.
- `Fill`: selection + fill material/texture.
- `Remip`: selection only (the algorithm derives the result) — but see inverse.
- `AddEntity`: `EntityType`, position (3×`i32`, fixed-point `DMF=16`), attr1..5
  (`i16`), reserved. **No floats** — see §11.7. Entity positions are integers at
  1/16 world unit, exactly as Cube2 sends them (`(int)(o.x * DMF)`).
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

**Compression: none, for now.** Cube2 zlib'd `packundo`, but our commands are
small and an edit is one command per gesture — a large paste is rare. The codec
has a `flags` field, so per-payload compression (zlib/zstd) can be added later
without a format break if a measured payload warrants it. Do not add it
speculatively.

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
stack is maintained locally by `EditorController`. Two exclusions, both by construction:

- another peer's commands (`AuthorPeerId != LocalPeerId`, §1.1), and
- **game-mode and script commands** — a crumbling wall or a scripted platform is
  never submitted through the local editor undo tracking.

Because undo tracking is owned by the local editor controller at submission time,
commands themselves do not need an artificial `CommandSource` tag stamped into
network packets or storage streams.

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

## 7. Map format: reference-based command history (Git-style)

## 7. Map format: reference-based FlatSharp packages (ADR 0009)

See [ADR 0009](adr/0009-reference-based-maps.md) for the full architectural
decision. The native format (vision.md §9) cleanly separates scene presentation, engine asset links, and authoritative binary geometry:

```
res://resources/maps/<map_name>/
├── Level.tscn              # Godot Scene: visual presentation (skybox, directional sun, fog, audio, rules)
├── manifest.tres           # Godot Resource: material/vslot bindings & model paths (engine asset links)
└── map.bin                 # FlatSharp Package: header, keyframes, entities, deltas
```

The map is a **directory, not a single file**. That matches Godot's external-resource idiom, enables native engine `.uid` tracking for textures and models, and packages cleanly into Steam Workshop delta diffs ([ADR 0009](adr/0009-reference-based-maps.md)).

### 7.1 Authoritative FlatSharp package (`map.bin`) & asset manifest (`manifest.tres`)

Rather than forcing Git to store multi-megabyte binary geometry churn on every commit or attempting to store 3D geometry in unmergeable text logs, our format adopts a unified FlatSharp binary architecture ([ADR 0009](adr/0009-reference-based-maps.md)):

- **Authoritative Binary Package (`map.bin`)**:
  - Encoded via FlatSharp (`schema.fbs`): zero-copy reads directly from byte spans, backward-compatible schema evolution.
  - Carries keyframe snapshots at `remip` boundaries (`KeyframePackage`) with dense pre-order cube streams (`ChunkKeyframe`), eliminating the need to replay unbounded history streams from genesis.
  - Carries active delta commands (`[Command]`) applied since the latest keyframe, world entities (`[EntityCmd]`), and environment settings (`SetEnvironmentCmd`).
  - **Instant load**: Deserializes 200,000 cubes in under 8 ms directly from `ReadOnlySpan<byte>` with zero per-node heap allocations.
  - **Git commit pegging**: Keyframes record the generating `git_commit` SHA, detecting drift between geometry and engine asset manifests on load.
- **Resource Manifest (`manifest.tres`)**:
  - Human-readable Godot `.tres` text resource mapping integer vslot, model, and sound indices to engine asset paths and properties.
  - Tracks Godot `.uid` dependencies cleanly, producing 1-line Git diffs when adding textures or models.
- **Unified FlatSharp Command Pipeline ([ADR 0008](adr/0008-command-logic.md))**:
  - Eliminates custom variable-length integer codecs (`WireCodec` / `putint`) and duplicate command representations in favor of the single FlatSharp `Command` table.

`Level.tscn` holds engine scene-tree visual nodes (`DirectionalLight3D`, `WorldEnvironment`, ambient audio, mode rules):

```
Level.tscn
  ├─ Level : MapLevel
  │    Manifest = ExtResource("manifest.tres")
```

Godot scenes store external resources by path and only inline them if the path is
cleared. **`Level.tscn` never inlines geometry or entities**.

### 7.2 Entities are command-versioned runtime projections, not scene data

Entities (`MapEntity`) are stored in the base snapshot and mutated in the same
command stream as cubes via `WorldOp.AddEntity`, `WorldOp.RemoveEntity`, and
`WorldOp.EditEntity` (using deterministic `1/16`th-unit fixed-point positions,
`FixedPoint.DMF = 16`). `IWorld.Apply` mutates `IWorld.Entities`, and
`WorldSnapshot.Hash()` hashes both the octree and `Entities` into every commit's
`TreeHash`.

At load time, `MapLevel` materializes entity nodes via the existing builders with
`Node.Owner == null`, so a `PackedScene` never saves them. This mirrors the render
chunks: `MapLevel` builds chunks and entities as **views** of the command-versioned
world state.

Consequence: **hand-editing entities happens through the editor/command path,
never the Godot inspector.** `Level.tscn` holds only scene-level concerns
(environment, mode config, editor camera). This is the price of unified
replication and versioning, and it is worth it — the same command stream that
places an entity is what replicates, undoes, versions, and saves it.

### 7.3 Rules

- **Commits are the default save; compaction is optional.** Normal editor saves
  (`F2` / `MapStore.Commit`) append a `MapCommit` to `history.res` on top of
  `BaseRef`. Explicit compaction (`MapStore.SaveCompacted`) snapshots `HEAD` as a
  local `world.res` and truncates `history.res`.
- **Versioned maps.** `world.res` carries `WorldSnapshot.FormatVersion`; each
  command carries `CommandCodec.FormatVersion` and `WorldOp` (append-only enum);
  `history.res` carries its commit chain and `TreeHash`es. **Maps are versioned
  both at the binary format level and as a Git-style commit history.**
- **Diffable/reviewable.** A text encoding of the history (or a `.tres` sidecar)
  lets a map change be reviewed like a code change.
- **Demo/replay = same bytes.** A demo is `base snapshot ref + command log +
  movement stream`. Because edit bytes are already canonical, demos and saves
  share the codec (features.md #7). Visual A/B against the original becomes
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
  `.pck`/`.zip` via `ProjectSettings.load_resource_pack` ([vision.md §12](vision.md#12-modding--workshop)).
- **Writing** uses `ResourceSaver.Save(res, "user://.../world.res",
  ResourceSaver.SaverFlags.Compress)`; **reading** uses `ResourceLoader.Load`.
- For a shipping game set `application/config/use_custom_user_dir = true` so
  saves live under the game's own name rather than Godot's shared data dir.

**Hashing & distributing:** the hash is computed over the map **archive**, not
over individual files. Workshop (and our parity checks in vision.md §12)
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
dirty-chunk system ([ADR 0007 § Mesh in chunks](adr/0007-world-rendering.md#2-mesh-in-chunks-rebuild-incrementally)):

1. Command reports its affected `Aabb` (selection expanded by boundary — Cube2's
   `readychanges` expands by 1 to catch merges/t-joints).
2. `WorldChunkRenderer` marks overlapping chunk ids dirty (bitmask/set),
   `MarkDirty(Aabb, margin)`.
3. Per-frame budget re-meshes up to N dirty chunks (`OctaMesher.BuildChunk`,
   `OctaFaceWalker.WalkRegion`).
4. Collision reads the live octree immediately — no shape rebuild
   ([ADR 0007 § Decoupled collision](adr/0007-world-rendering.md#6-rendering-and-collision-are-decoupled)).

Determinism caveat: meshing must not affect world state. It doesn't — the octree
is the truth; meshes are derived. This keeps rollback cheap (re-mesh only dirty
chunks after replay).

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
> `RemipTest.cs`). Then **M2** (incremental meshing) and **M3** (command log).

**M2 — Incremental meshing (done).** `OctaMesher.BuildChunked` split into per-chunk
rebuild + dirty set + per-frame budget (`OctaMesher.BuildChunk`,
`OctaFaceWalker.WalkRegion`, `WorldChunkRenderer`); asserted by
`test/IncrementalMeshTest.cs`. Bakes in the rendering side early so edits are
visible while we build the rest.

**M3 — Command log + undo/redo (done).** `WorldOp`/`Command`/payloads,
`CommandCodec` (`var_to_bytes` over positional arrays, §5.1), `IWorld`/`OctaWorld`
apply seam (octree + entities), `CommandLog` (sequence + own-commands-only undo stack),
`CommandSink` (submit/receive/undo/redo, §5.5 rules). Offline; asserted by
`test/CommandSystemTest.cs`. **Note:** undo/redo tests compare a
subdivision-invariant *content* snapshot, not the `OgzGeometry` structural
checksum — `LookupCube` subdivides as a side effect, so equivalent octrees can
hash differently (§9).

**M4 — Editor UI (Godot).** Core editing mechanics first, then GUI:

- **M4a — cursor cast**: eye ray → `(lu, gridsize, orient, cur, cor, corner)`,
  `editmoveplane`/`rayboxintersect`/`normalizelookupcube` (`World/Editor/`).
  **Done** — `CursorGeometry` + `EditCursor`, asserted against the engine by
  `test/EditCursorTest.cs` (the world ray cast that supplies `rayDist` is wired
  via `OctaRaycast` — see M4d).
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

  **Input binds wired** (Cube2 `editbind`). Keyboard binds are **`InputMap`
  actions** (`editor_*` in `project.godot`), not hardcoded keys, so they are
  remappable: hold `G` grid, `F` face push/pull, `Q` corner push/pull,
  `R` rotate, `Y` texture, `1`-`4` entity-attr/texture-param combos: scroll with
  those held = the `delta_use` namespace. `X` flip, `SPACE` cancel, `DELETE`
  delcube, `KP4-9` nudge selection, `C`/`V` copy/paste (clipboard is
  client-local), `B` cut, `U`/`I` undo/redo, `O` remip, `T` re-apply texture,
  `[`/`]` pushsel, `\`/`;`/`'` apply air/clip/death material, `P` pastehilite,
  `6` allfaces, `N` selcorners. Mouse gestures (`MOUSE1` box-select, `MOUSE2`
  extend, `MOUSE3` selcorners, `MOUSE6/7` cut, wheel/trackpad scroll) stay raw
  events — they need press+release and analog scroll, which `InputMap` actions
  cannot carry (vision.md §11.1).
  **macOS note:** trackpad/Magic-Mouse scrolling arrives as `InputEventPanGesture`,
  not `WheelUp/Down` — both are handled.

  **`delta_do` dispatch (done).** `EditorController.DeltaDo` ports the universal
  delta (`data/stdlib.cfg`): a held `delta_use` key selects the *active* modifier
  (first pressed wins, matching the original's `delta__active` guard), and a wheel
  notch dispatches on it — `delta__edit_@active @dir`. The **full id table**
  (`DeltaModifier`, ids 0-7 / 11-15 / 21-24 / 31-35, identical to the original) is
  routed; the ids whose subsystems are not ported yet (height-map brush, entity
  attributes, vslot parameters, the combos) are **explicit no-ops** so their binds
  cannot silently fall through to the fill default. Asserted by
  `test/DeltaDispatchTest.cs`.

  **Not wired yet:** the texture/material picker UI (M4e) — the texture/material
  wheel ops walk the current index and apply it until the picker lands; persistent
  user remaps via `user://input.cfg` (vision.md §11.1, with the settings UI);
  remappable mouse gestures.

  **Done (engine work):** `OctaRay.RayCube` is now a port of
  `physics.cpp:raycube` (hierarchical DDA + deformed-cube clipping planes) in
  `World/Physics/OctaRaycast.cs`, with the render-hull `genclipplanes` in
  `OctaClipping.GetRenderClipPlanes`. Ground truth lives in `tools/rayprobe/`;
  asserted by `test/OctaRaycastTest.cs` and the re-baselined `test/OctaRayTest.cs`.
- **M4e — GUI**: texture/material pickers, entity placement, keybinds via
  `InputMap`. Features.md #5 checklist.

The wheel is bound like Cube2: `InputMap` actions for the modifier keys, plus a
single wheel/trackpad path that dispatches through `delta_do` on the active
modifier (see the `delta_do` note below).

**M5 — Reference-based save/load & commit history (done).** `WorldSnapshot`
encoder/decoder (canonical child-order octree traversal + entity blob, version
field, content hash), Godot `WorldResource`/`HistoryResource` (with `BaseRef`,
`HeadHash`, and `MapCommit` chain where each commit's `TreeHash` is the resulting
`WorldSnapshot.Hash()`), shipped canonical `res://resources/maps/empty_map/`,
zero-copy reference initialization (`MapStore.InitFromReference`), commit append
(`MapStore.Commit`), historical checkout (`MapStore.Checkout`), compaction, and
archive content hash ([ADR 0009](adr/0009-reference-based-maps.md)).
Round-trip/replay/commit tests in `test/WorldSnapshotTest.cs` and
`test/MapFormatTest.cs`; church51 reloads with an identical geometry checksum, and
`scenes/empty_map.tscn` loads `MapLevel` referencing the canonical `empty_map`
(`scenes/editor_scene.tscn` stays the OGZ dev viewer on church51).

**M6 — Networking.** `WorldNetwork` RPC nodes, channels, authority, sequence,
snapshot transfer, late join. Headless multi-client tests. *Unblocks coop edit.*

**M7 — Prediction/rollback.** Snapshot ring, optimistic apply, reconcile.
*Can ship coop edit without this first; optimizes feel.*

**M8 — Prefabs, recipes, cross-map paste.** `ReplaceBlock` content resources,
rotation, recipe API, Workshop packaging hook. *The payoff layer.*

**M9 — Runtime geometry for game modes.** Modes submit `WorldCommand`s
through the same sink (crumbling walls, scripted platforms; vision.md §6.1).
They are never undoable and ride the edit channels. *Shares everything above; no new mechanism.*

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
3. ~~**Text vs binary history log**~~ — **decided: binary.** The command
  history is a binary blob (`.res`/custom). A text sidecar may be *generated*
  for VCS review, but is not the on-disk format. Matches §7.3.
4. ~~**Prediction policy by source**~~ — **decided:** governed by author peer session.
  Client editor submissions are optimistic (local stamp, reconcile);
  server-authored geometry (game mode / script events) is applied on broadcast. No
  `CommandSource` enum needed in the command struct or wire packet. (See authority-and-latency.md §7.)
5. ~~**Undo across remip**~~ — **decided (§5.4, §5.5):** `Remip` is a
   pass-through replicated op (payload `{selection, mipvis}`, deterministic
   integer re-execution). Undo is a normal `ApplyInverse` command, capturing the
   inverse at undo time; it is never transmitted and needs no special case.
6. ~~**Block compression**~~ — **decided: none for now.** Edit commands are small
   and infrequent (that is the design), so zlib would be premature. The codec
   already carries a `flags` field, so compression can be added later behind the
   seam with no format break. Revisit only if a measured payload says so.
7. ~~**Float determinism**~~ — **closed by not having floats.** The wire carries
   **no floats**: positions are fixed-point integers (Cube2's own scheme,
   `DMF = 16` → 1/16 world unit, i.e. `(int)(pos * 16)`), attributes are `i16`,
   and cubes/selection are already integers. Determinism is therefore trivial on
   the apply paths, and pass-through `Remip` (§5.4) is hardened. Verify: the
   codec has no `float`/`Single` today (`World/Commands/WorldCommandCodec.cs`).
8. ~~**Permission/authority model**~~ — **decided: it is configuration.** The
   server decides what each mode permits, enforced in `Sink.MayApply(peer, cmd)`:
   - **edit mode**: `Editor` commands from connected peers (subject to a
     master/admin gate if we add one, like Cube2's `mastermode`);
   - **game mode**: `Editor` commands rejected; only `GameMode`/`Script`
     geometry allowed.

   This is a mode/permission config, not a new mechanism.
9. ~~**Prediction scope (M6/M7)**~~ — **decided, and much smaller than feared.**
   The rule stands (movement predicts only against the world at the last
   **acknowledged** `WorldCommand` sequence) — see
   [authority-and-latency.md §7](authority-and-latency.md#7-the-rule-this-forces-prediction-scope-decided).
   But #8 shrinks the risk to near-zero:
   - In **game mode there are no editor edits**, so the in-flight-`WorldCommand`
     race (source #1) does not arise during play — the map is static, as in CS:GO.
   - Any runtime mutation (crumbling wall, race door) is `GameMode`/`Script`,
     **run by the server** and broadcast in sequence order — a bounded, ordered
     event, not a client-originated race.
   - A **map hash** gate (clients and server agree on the map before play) removes
     the whole "client has geometry the server doesn't" class at the *session*
     level.

   Residual risk is only "a scripted mid-game mutation races a player's movement",
   which the acked-sequence rule makes a few-ms-late effect, not a correction. The
   rule is therefore *required* (it is what makes the risk small) but **cheap to
   validate**: a test that movement prediction converges across a server-authored
   scripted mutation, rather than an open-ended investigation. Reconciliation
   policy (snap / smooth) is a minor, local choice (see #1).
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

- **Rule 1:** A's Ctrl-Z is about `E_A1` (A's own editor command). It is
  permitted in principle; B's `E_B1` is not on A's stack and cannot be undone by
  A. Nor could a game-mode command (e.g. a crumbling wall) ever be undone — it
  is never captured in the editor's undo stack.
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
├── WorldOp.cs              # append-only op enum (CommandSource eliminated)
├── Command.cs              # command + typed payloads (geometry + entities)
├── CommandCodec.cs         # canonical wire/save/demo encoding
├── Selection.cs            # selinfo / block3 port (done, M0)
├── CommandSink.cs          # submit/receive/apply, sequence assignment
├── CommandLog.cs           # append-only log + own-editor-commands undo stack
├── IWorld.cs               # Apply + Capture seam (OctaWorld: Octa + Entities)
└── FixedPoint.cs           # 1/16 world-unit integer quantization (DMF=16)
World/OctaEdit.cs          # structural primitives + ported edit algorithms (M1)
World/Entities/
└── MapEntity.cs           # the stored entity: float in memory, fixed-point on disk/wire
World/Schema/
└── schema.fbs             # Authoritative FlatBuffers schema for commands & maps (ADR 0008, ADR 0009)
World/Commands/
├── Command.cs             # FlatSharp partial class: domain factories & helpers (ADR 0008)
├── CommandSink.cs         # captures undo pre-state, applies forward, records in log (ADR 0008)
├── CommandLog.cs          # append-only log with local author-scoped undo/redo stacks (ADR 0008)
└── FixedPoint.cs          # 1/16 world-unit integer quantization (DMF=16)
World/Runtime/Formats/Save/
├── MapStore.cs            # FlatSharp map package load/save & delta compilation (ADR 0009)
├── KeyframePackage.cs     # High-level domain wrapper over FlatSharp keyframes (ADR 0009)
├── ResourceManifest.cs    # Godot Resource wrapper for manifest.tres asset bindings (ADR 0009)
├── ManifestBuilder.cs     # Manifest construction and catalogue synchronization
└── MapLevel.cs            # Scene node: runtime projections for entities & lights (ADR 0008, 0009)
resources/maps/empty_map/
├── Level.tscn             # canonical base empty map scene
└── map.bin                # canonical FlatSharp base keyframe package (ADR 0009)
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
