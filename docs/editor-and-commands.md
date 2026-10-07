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

### 1.1 Command

```csharp
namespace SauerbratenRevival.World.Commands;

/// Stable numeric id — part of the wire + save format. Never reorder; append only.
public enum EditOp : ushort
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
```

```csharp
public sealed class EditCommand
{
    public EditOp Op;
    public ulong Sequence;        // monotonic; total order in the log
    public int AuthorPeerId;      // who issued it (attribution, not authority)
    public byte[] Payload;        // op-specific, deterministic encoding (§5)
}
```

**Principles**

- **Deterministic**: applying a command to a given world state always yields the
  same result. No wall-clock, no RNG seeded per client, no host floats. RNG, if
  any op needs it, carries its seed in the payload.
- **Atomic at the *selection* granularity, not the cube**: `Fill` over a 64³
  selection is *one* command (one payload, one undo entry, one network message),
  not 262144. This is the key divergence from Cube2's per-cube `pastecube` loop
  and the reason large patches sync fine.
- **Inverse is derived, not stored**: selection-scoped ops know the old block
  (they captured it as their pre-state); the inverse is "restore that block".
  See §5.3.

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

---

## 2. The WorldCommandSink — one seam, many drivers

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
                 UndoStack       NetworkSender    DemoRecorder
                                 (RPC/ENet)       (same bytes)
                     │
                 applyInverse
```

- **Single entry point.** Nothing mutates the octree directly; everything goes
  `sink.Submit(cmd)`. Even game-mode "crumbling wall" schedules `RemoveCube`
  commands. This makes the world *auditable* and replayable by construction.
- **Apply is the same code path** on the issuing client, the server, and every
  peer. There is no "local edit" vs "remote edit" divergence.
- **The log is the source of truth**; the octree is a materialized view.

Interfaces (sketch):

```csharp
public interface IWorld {                 // the mutable octree surface
    void Apply(EditCommand cmd);          // deterministic; mutates Octa + entities
    WorldSnapshot CaptureBlock(Selection sel);
}

public interface ICommandSink {
    void Submit(EditCommand cmd);         // local origin: validate → log → apply → replicate
    void Receive(EditCommand cmd);        // remote origin: log → apply (no echo)
}
```

`IWorld.Apply` is deliberately small. All the Cube2 algorithms we port
(`pastecube`, `fill`, `remip`, `replacenear`, face/texture ops) live behind it
in a ported `OctaEdit` module, one handler per `EditOp`.

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

| Channel | Content | Mode |
| --- | --- | --- |
| `EDIT_REQUEST` | client → server intents | Reliable |
| `EDIT_BROADCAST` | server → all canonical commands | Reliable |
| `SNAPSHOT` | server → one client, chunked world snapshot | Reliable, chunked |
| `MOVEMENT` | player physics state (features.md #8) | Unreliable (latest wins) |

Edit commands are **always reliable and ordered**. They are small and infrequent
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
        var c = EditCodec.Decode(cmd);
        if (!_authority.MayApply(Multiplayer.GetRemoteSenderId(), c)) return;
        _sink.Submit(c);                       // assigns Sequence, applies locally
        Rpc(MethodName.CmdBroadcast, EditCodec.Encode(c)); // echo to all (see note)
    }

    [Rpc(MultiplayerApi.RpcMode.Authority,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable,
         Channel = 2)]
    private void CmdBroadcast(byte[] cmd) => _sink.Receive(EditCodec.Decode(cmd));
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

One `byte[]` per command, produced by `EditCodec`:

```
[u16 op][u32 flags][u64 sequence][u64 payloadLen][payload...]
```

`payloadLen` is redundant with the frame but lets a command be self-contained for
save files and demos. Godot's `byte[]` RPC variant packs this efficiently; if we
need more, a `PackedByteArray` or a `StreamPeerBuffer` over a `PackedByteArray`
avoids per-message allocation. Prefer **fixed little-endian** encoding, not
Godot `var2bytes`, so the save/demo/network bytes are identical and portable.

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
EditCommand { Op = ReplaceBlock, Payload = { selection, newBlock, oldBlock } }
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

`Remip` (merge/discard children, recompute cube sizes — Cube2's `remip`) is
deterministic *given the same input octree and selection*, so it can be a
"derive the result" op. But its inverse is not a simple op swap; treat it as a
destructive op that captures the old block, OR forbid undo across remip and
snapshot instead. **Decision: `Remip` captures the old block** for uniformity;
the block is often small (remip on tight selections). Documented cost: remip undo
is the largest payload; acceptable.

---

## 6. Prefabs & recipes as commands

With `ReplaceBlock` as the universal carrier:

- **Prefab** = a named `newBlock` (+ its entity commands), stored as a `Resource`
  (`.tres`/`.res`) — *not* `.obr`. It is content: shipped, Workshop-uploaded,
  hash-verified like any other asset.
- **Recipe** = a *parameterized* sequence of commands (a small data script) that
  stamps geometry/entities into a region: e.g. "pillar(height, material)" emits
  N `AddCube`/`ReplaceBlock`s. Recipes are a `Resource` with parameters and a
  `Generate(Selection) -> IEnumerable<EditCommand>` method (data-driven, no
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
├── history.res           # appended EditCommands since world.res (optional)
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
  carries `EditOp` (append-only enum). A newer file opened by an older build can
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

---

## 10. Implementation order (milestones)

Ordered so each step is independently verifiable and unblocks the next.

**M0 — Selection model (`selinfo` port).** `Selection` struct: origin, size, grid,
orient, corner tracking, plus `blockcopy`/`block` capture. Pure data, heavily
unit-tested. *No dependencies.*

**M1 — `IWorld.Apply` + op handlers (single-player).** Port `pastecube`, fill,
face texture/material, add/remove, remip against `Octa.cs`. Test via
round-trip/inverse checksums. This is the editor's engine, no UI/network yet.

**M2 — Incremental meshing.** Refactor `OctaMesher.BuildChunked` into per-chunk
rebuild + dirty set + per-frame budget. Bakes in the rendering side early so
edits are visible while we build the rest. *Prereq for any live editing.*

**M3 — Command log + undo/redo.** `EditCommand`, `EditCodec`, `WorldCommandLog`,
undo/redo via inverse. Editor operations now go through the sink. Still offline.

**M4 — Editor UI (Godot).** Grid/plane cursor (port `editmoveplane` +
`rendereditcursor`), selection extend/rotate/flip, texture picker, material
picker, entity placement, keybinds via `InputMap`. Features.md #5 checklist.

**M5 — Save/load in native format.** `WorldSnapshot` encoder/decoder; `world.res`
+ `history.res`; version field; compaction; archive packaging + content hash
(§7.4). Round-trip tests. *Unblocks #3 importer too.*

**M6 — Networking.** `WorldNetwork` RPC nodes, channels, authority, sequence,
snapshot transfer, late join. Headless multi-client tests. *Unblocks coop edit.*

**M7 — Prediction/rollback.** Snapshot ring, optimistic apply, reconcile.
*Can ship coop edit without this first; optimizes feel.*

**M8 — Prefabs, recipes, cross-map paste.** `ReplaceBlock` content resources,
rotation, recipe API, Workshop packaging hook. *The payoff layer.*

**M9 — Runtime geometry API for game modes.** Expose the sink as a safe,
data-driven API for modes (crumbling walls etc., architecture.md §1). *Shares
everything above; no new mechanism.*

Milestones M0–M3 are the real work and are all testable without a window. M4 is
the visible editor. M5+M6 are the multipliers. M8 is the differentiator.

---

## 11. Open questions / decisions to make

1. **Snapshot cadence for rollback**: every N commands, every T ms, or every
   frame? Trade memory vs rollback latency. Suggest: ring of 32 snapshots,
   promote-to-full when a block edit exceeds a size threshold.
2. ~~**Entity authority detail**~~ — **decided (§7.2):** entities are runtime
   projections (`Owner == null`), never serialized into `Level.tscn`. Hand-editing
   entities is editor/command-path only.
3. **Text vs binary history log**: text is diffable/reviewable but larger and
   slower. Suggest: binary `.res` for load, generate a text sidecar for VCS
   optionally (like Godot's `.tres` for `.res`).
4. **Per-op prediction policy**: which ops are optimistic (editor stamps) vs
   server-only (scripted geometry)? Make it a property of `EditOp`.
5. **Undo across remip**: capture-the-block (proposed) vs snapshot barrier.
6. **Block compression**: zlib level and threshold; must be identical across
   platforms for the parity hash (zlib is, but pin the library/level). Note this
   is *inner* block compression inside the codec, separate from the *outer* map
   archive (§7.4), whose hash is the parity unit.
7. **Float determinism**: positions are f32 on the wire; confirm no accumulation
   of arithmetic in apply paths (only integer octree math + exact f32 copies).
8. **Permission/authority model**: who may edit (all coop clients? server
   config?), and how it interacts with the edit lock and game modes.

---

## 12. File layout (planned)

```
World/Commands/
├── EditOp.cs              # append-only op enum
├── EditCommand.cs         # command + codec
├── EditCodec.cs           # canonical wire/save/demo encoding
├── Selection.cs           # selinfo / block3 port
├── WorldCommandSink.cs    # submit/receive/apply, sequence assignment
├── WorldCommandLog.cs     # append-only log + snapshot ring
├── IWorld.cs              # Apply + CaptureBlock seam
└── Ops/                   # one handler per EditOp (ported algorithms)
    ├── AddRemoveCube.cs
    ├── FaceOps.cs         # texture/material/flip/rotate/replace
    ├── Fill.cs
    ├── Remip.cs
    ├── EntityOps.cs
    └── ReplaceBlock.cs    # the universal carrier (paste/prefab/undo/fill)
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

- **One mechanism**: mutation = command = undo = network = demo = prefab.
- **Selection-scoped, block-capturing commands** fix Cube2's big-patch/prefab
  limitation and give undo for free.
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
