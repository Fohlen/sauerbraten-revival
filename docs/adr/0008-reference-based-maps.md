# ADR 0008: Reference-Based Map Format (Git-Style Merkle Octree, Object Pack & Commit History)

## Status

**Proposed** (pending Stage 2 implementation and benchmarks on `church51`).
Extends the native map-directory format defined in
[editor-and-commands.md §7](../editor-and-commands.md#7-map-format-reference-based-command-history-git-style)
and [vision.md §9](../vision.md#9-level-format).

---

## Context

In Sauerbraten Revival every world mutation — whether an octree cube edit or an
entity placement/modification — is a deterministic, serializable [`Command`](../../World/Commands/Command.cs)
processed through [`CommandSink`](../../World/Commands/CommandSink.cs) and encoded
by [`CommandCodec`](../../World/Commands/CommandCodec.cs).

When designing versioned maps ("like Git"), several requirements and architectural
constraints must be satisfied simultaneously:

1. **Replaying the full command history on load is impractical and lossy.**
   `OctaEdit.Remip` is 100% deterministic in the forward direction (pure integer
   arithmetic), but it is **lossy**: it collapses 8 children into 1 parent leaf
   (`DiscardChildren`), drops interior occluded child face textures, and rewrites
   `Visible`, `Merged`, and `CubeExt` across the tree. Trying to reconstruct the
   world by replaying commands or inverting them across a `Remip` cannot
   faithfully reconstruct the pre-remip octree node layout without intermediate
   state.
2. **Storing a full `WorldSnapshot` per commit causes linear file size bloat.**
   If every commit stored a full uncompressed snapshot of the octree, dozens of
   commits on a detailed map would multiply the file size dramatically.
3. **An octree is a natural Merkle tree.**
   An octree is a hierarchy of 8-child nodes (`Cube[8]`). By representing each
   `Cube[8]` node and the entity list as **content-addressed, packable
   intermediate objects**, unchanged subtrees between commits share identical
   hashes and are stored once.
4. **Collision resistance and identity integrity.**
   The system must not use weak or non-cryptographic hashes (like FNV-1a) for
   identity. Collisions in a Merkle tree return corrupted subtrees, and in a
   server-authoritative multiplayer game or Steam Workshop ecosystem, weak hashes
   expose security vulnerabilities.
5. **Distinct commit vs. tree identities.**
   In Git, a `Tree` is the directory state, while a `Commit` is an authored,
   timed, parent-linked revision pointing to that tree. Reverting to a prior state
   or authoring a new commit with identical geometry must produce distinct
   commit identities while referencing the same tree.
6. **Programmatic generation baseline.**
   [`EmptyMap`](../../World/Editor/EmptyMap.cs) and [`scenes/empty_map.tscn`](../../scenes/empty_map.tscn)
   remain as the programmatic octree generation example (and future template for
   terrain generation demos). The editor dev viewer ([`scenes/editor_scene.tscn`](../../scenes/editor_scene.tscn))
   uses the native reference-based format via [`MapLevel`](../../World/Runtime/Formats/Save/MapLevel.cs).

---

## Decision

### 1. High-Performance 128-Bit Content Addressing (`ObjectId` via `XxHash128`)

Rather than SHA-256 (which is heavy and overkill for non-adversarial octree
Merkle hashing) or 64-bit FNV-1a (which has birthday collisions around $2^{32}$
elements), content addressing uses **`System.IO.Hashing.XxHash128`**:

- **128-bit (16 bytes)**: Represented as a compact, stack-allocated
  `readonly struct ObjectId(ulong High, ulong Low)`. Fits in registers with
  zero heap allocations and formats as a clean 32-character hex string.
- **Extreme throughput**: Vectorized with SIMD instructions (~15–30 GB/s),
  running at memory bus speed. Hashing thousands of octree nodes takes fractions
  of a millisecond.
- **Collision resistance**: With a 128-bit state space ($2^{128}$), generating
  quintillions of octree subtrees carries virtually zero accidental collision risk.
- **Verification on read**: When an object is fetched or transferred, its payload
  is hashed against its `ObjectId`. Corrupted data throws `CorruptObjectException`.

### 2. Canonical Encoding & Excluded Derived Data

To ensure identical geometry always hashes to the exact same `ObjectId` across
different platforms, compilers, and lighting passes:

- **Authoritative fields for an `OctaNode` (`Cube[8]`)**:
  - 8 cubes in fixed octant order `0..7`.
  - For each cube:
    - 3 edge masks (`Faces[0..2]`),
    - 6 face textures (`Texture[0..5]`, `ushort`),
    - `Material` (`ushort`).
    - If subdivided (`Children != null`): the child's `ObjectId` (20/32 bytes).
    - If leaf (`Children == null`): null `ObjectId`.
    - If author-specified surface geometry exists: author vertex offsets.
- **Explicitly Excluded from `OctaNode` Hash & Encoding**:
  - `Merged` (runtime mesher merge cache derived from geometry/textures).
  - `Visible` (runtime occlusion bitmask derived by `visibletris`/`visibleface`).
  - Runtime lighting caches (`CubeExt.Surfaces` vertex buffers, runtime normals).
  - Both `Merged` and `Visible` are transient runtime caches rebuilt on-demand
    during meshing and are not part of the canonical geometry identity.
- **Little-Endian standard**: All multi-byte integers are encoded in Little-Endian.
  Floating-point numbers are strictly forbidden in geometry and entity records.
- Every object carries a leading 1-byte `FormatVersion`.

### 3. Merkle Object Types

| Object Kind | Tag | Content |
| --- | --- | --- |
| **`OctaNode`** | `0x01` | One `Cube[8]` node. Subdivided octants carry child `ObjectId`s. |
| **`EntityList`** | `0x02` | Array of entities sorted by stable `EntityId` (see §6). Fixed-point coordinates (`DMF = 16`). |
| **`WorldTree`** | `0x03` | Root descriptor: `(uint FormatVersion, int WorldScale, ObjectId RootNodeId, ObjectId EntityListId)`. |
| **`Commit`** | `0x04` | Revision object: `(ObjectId ParentId, ObjectId TreeId, ObjectId CommandsId, long TimestampUtcMs, string Author, string Message)`. |
| **`CommandBlob`**| `0x05` | Serialized `Command[]` array applied in this commit (advisory & audit log). |

### 4. Transparent Object Store (`MapObjectStore` in `Resource`)

Rather than hand-rolling an opaque custom packfile with binary-search offset
tables, the object store uses standard, robust Godot/.NET primitives:

- **In-Memory & Storage**: `HistoryResource` (and `WorldResource`) stores:
  ```csharp
  // Dictionary mapping ObjectId hex string -> canonical object bytes
  [Export] public Godot.Collections.Dictionary<string, byte[]> Objects { get; set; } = new();
  ```
  Godot's `ResourceSaver.Save(res, path, Compress)` compresses the entire resource
  using standard Deflate/Zstandard.
- **In-Memory `MapObjectStore` lookup**:
  In C#, `MapObjectStore` wraps the dictionary and supports fallback chaining:
  ```csharp
  public sealed class MapObjectStore
  {
      private readonly Dictionary<ObjectId, byte[]> _objects = new();
      public MapObjectStore? BaseStore { get; set; }

      public bool TryGet(ObjectId id, out byte[] data)
      {
          if (_objects.TryGetValue(id, out data!)) return true;
          if (BaseStore != null) return BaseStore.TryGet(id, out data);
          return false;
      }
  }
  ```
  Hash lookup is $O(1)$ memory lookup, with zero binary search, endianness bugs,
  or custom seek offset tables. For typical maps (~500 KB to a few MB total), the
  entire active object graph fits comfortably in memory.
- **Delta storage**: A child map's `Objects` dictionary only stores entries that do
  not already exist in `BaseStore` (`res://resources/maps/empty_map/world.res`).
- **Pluggable interface**: The world materializer only interacts with
  `IObjectStore`. If a future 4K gigabyte-sized world ever requires on-disk loose
  files or standard `System.IO.Compression.ZipArchive`, it can swap in behind the
  same interface without touching the Merkle octree logic.

### 5. `BaseRef` Immutability and Verification

To prevent upstream changes in `empty_map` from corrupting dependent maps:
- `BaseRef` contains **both** the path and the expected `TreeId`:
  ```
  BaseRef: "res://resources/maps/empty_map/world.res@sha256:<expected-tree-id>"
  ```
- On load, `MapStore` verifies that `BaseRef` resolves and matches `ExpectedTreeId`.
  If the base map was modified or missing, it fails loudly with
  `BaseMapMismatchException`.
- `res://` is read-only in exports. Maps created or edited in-game always save to
  `user://maps/<id>/`.

### 6. Stable Entity Identity & Entity Commands

Array indexing (`RemoveEntity(Index)`) is fragile under concurrent edits and
ordering shifts.
- Every entity has a **stable 64-bit `EntityId`** assigned at spawn/creation.
- Entity commands:
  - `WorldOp.AddEntity` (`30`): `(ulong EntityId, byte Type, int FixedX, int FixedY, int FixedZ, short Attr1..5)`
  - `WorldOp.RemoveEntity` (`31`): `(ulong EntityId)`
  - `WorldOp.EditEntity` (`32`): `(ulong EntityId, byte Type, int FixedX, int FixedY, int FixedZ, short Attr1..5)`
- `EntityList` stores entities sorted by `EntityId` for canonical hashing.
- Unchanged entity lists across commits share the same `ObjectId`.

### 7. Authoritative Tree vs. Advisory Command Log

- **The `WorldTree` is authoritative**: Map loading materializes the octree and
  entities directly from the `WorldTree`'s Merkle DAG in $O(\text{nodes in tree})$.
  No command execution or replay is performed on load.
- **The `CommandBlob` is an advisory audit and replay log**: It documents *how*
  the transition was achieved for undo history, networking replication, and diffs.
- **Verification in CI and tests**: `MapStore.VerifyCommit(commitId)` runs the
  commands from `Parent.TreeId` and asserts that the resulting tree produces the
  exact `Commit.TreeId`.

### 8. Compaction & Garbage Collection

- When commits are reverted or dropped, unreferenced `OctaNode` and `EntityList`
  objects may remain in the dictionary.
- `MapStore.CollectGarbage(mapDir)`:
  1. Gathers all reachable `ObjectId`s by walking `HEAD` and its commit ancestry
     back to root.
  2. Retains only reachable keys in `Objects`.
  3. Saves the cleaned resource.
- `MapStore.SaveCompacted(mapDir)`:
  Rebases `HEAD` into a standalone, unparented root commit with a self-contained
  `Objects` store, decoupling it from `BaseRef`.

---

## Consequences

**Positive**

- **Cryptographic safety**: SHA-256 eliminates accidental and intentional hash
  collisions.
- **True Git revision graph**: Distinguishes `Tree` (state) from `Commit`
  (history revision), allowing clean linear history, reverts, and undo.
- **No command replay on load**: World materialization is bounded by $O(\text{nodes in tree})$
  rather than $O(\text{commits} \times \text{commands})$.
- **Lossy `Remip` is fully supported**: The resulting merged octree is captured as
  a Merkle tree; checking out an earlier pre-remip commit restores the exact
  unmerged node structure.
- **Small commits**: Unchanged subtrees share `ObjectId`s across commits and with
  `BaseRef`.
- **Stable entities**: Fixed-point coordinates and stable 64-bit IDs prevent list-shift
  bugs in coop edit.

**Trade-offs**

- Walking the Merkle tree to compute SHA-256 hashes adds a minor CPU overhead on
  commit (must be benchmarked in Stage 2 against `church51`).
- Transient caches (`Merged`, `Visible`) must be rebuilt during meshing rather
  than stored directly in the hash identity.
- Linear history only; branching/merging is not supported.

---

## Key Source Files

- `res://resources/maps/empty_map/` — shipped canonical base map pack.
- `World/Runtime/Formats/Save/ObjectId.cs` — 128-bit `XxHash128` identifier (`readonly struct ObjectId`).
- `World/Runtime/Formats/Save/MapObjectStore.cs` — transparent `IObjectStore` with base fallback.
- `World/Runtime/Formats/Save/HistoryResource.cs` — `Commit` objects, delta `Objects` dictionary, and commit chain.
- `World/Runtime/Formats/Save/MapStore.cs` — load, commit, checkout, GC, verify.
- `World/Runtime/Formats/Save/MapLevel.cs` — runtime viewer node.
- `World/Commands/` — `Command`, `CommandCodec`, `CommandLog`, `CommandSink`, `WorldOp`.
