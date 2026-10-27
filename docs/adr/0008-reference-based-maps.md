# ADR 0008: Map Architecture, Delta Packaging, and Wire Protocol via FlatSharp (FlatBuffers)

## Status

**Proposed** (Overhauls and supersedes the earlier hybrid Git-text `.tres` and manual integer-packing proposal).  
Extends the native map-directory format defined in [editor-and-commands.md §7](../editor-and-commands.md#7-map-format-reference-based-command-history-git-style) and [vision.md §9](../vision.md#9-level-format).

---

## Context & Motivation for Overhaul

Our previous revision of ADR 0008 attempted to balance Git diffability and network efficiency by splitting map storage into a human-readable Godot text command log (`history.tres`) and a low-latency variable-integer network codec (`WireCodec` via Sauerbraten's `putint`).

Upon closer practical evaluation against production requirements, both halves of that design proved fundamentally flawed:

### 1. The Human-Readable `.tres` Fallacy
* **3D Geometry Edits Do Not Diff Meaningfully in Git**: Real level-editing sessions generate tens of thousands of micro-operations (nudge brush, push face, paint texture, drag selection). A pull request containing 40,000 lines of coordinates like `EditFace: o=(512, 512, 128) s=(1, 1, 1) dir=1` is incomprehensible to human reviewers.
* **Non-Mergeable Spatial History**: Standard Git 3-way line merges cannot reconcile concurrent 3D spatial operations. Merging two branches of text commands blindly produces spatial collisions, invalid geometry, and broken octree states.
* **Format Impedance Mismatch**: Godot's text resource format (`.tres`) is engineered for inspector properties, materials, and small node graphs. Storing streaming command logs or high-density octree data in `.tres` creates massive file bloat, slow line-by-line regex/text parsing on launch, and excessive garbage collection pressure.

### 2. Manual Integer Packing is a Solved Problem
* Sauerbraten's original `putint` bitfield twiddling was written in the early 2000s for bandwidth-constrained dial-up connections.
* Hand-rolling custom variable-length integer codecs, bitfield packing, and manual byte-blitting in C# (`WireCodec` / `MemoryMarshal`) is error-prone, fragile to schema evolution, and duplicates effort already solved by modern, battle-tested serialization systems.

### 3. The `Remip` Barrier & Intermediate Binary Blobs
* In Cube 2 / Sauerbraten, `remip` (and `mpremip`) is an aggressive, lossy, structural geometry simplification operation (`OctaEdit.Remip`). It analyzes sibling cubes across mip levels, removes redundant internal divisions, and merges uniform geometry into parent nodes.
* **The Long-History Replay Problem**: If a map format relies purely on replaying a linear command log from genesis ($T_0$), replaying 100,000 edits on load is prohibitive. Furthermore, replaying thousands of micro-edits that were subsequently obliterated or merged by a `remip` is pure waste.
* **Undo & Checkpoint Boundaries**: Forward `remip` is derivation-only and destroys intermediate sub-cube history. Seeking across history, testing checkpoints, or rolling back requires actual geometric snapshots.
* **Conclusion**: We cannot treat map storage as an unbounded linear stream of micro-commands. We must support storing **intermediary binary octree blobs (keyframes/checkpoints)** at `remip` boundaries and major edit milestones.

---

## Decision

We adopt **[FlatSharp](https://github.com/jamescourtney/FlatSharp)** (the high-performance, idiomatic Google FlatBuffers implementation for C# / .NET) as the **single unified serialization foundation** for:
1. **Network Wire Protocol**: Low-latency, zero-copy coop-editing packets over ENet.
2. **Recorded Sessions & Replays**: Streaming command logs recorded directly to disk.
3. **Map Packages & Checkpoints**: Fast-loading binary packages with intermediary octree blobs.
4. **Steam Workshop Distribution**: Compact delta packages / diffs referencing base maps.

```mermaid
graph TD
    FBS[FlatBuffers Schema .fbs<br>Single Source of Truth] --> FlatSharp[FlatSharp C# Compiler]
    
    FlatSharp --> Wire[Live Network Packet<br>ENet / Zero-Copy Span]
    FlatSharp --> Rec[Recorded Session / Demo<br>Appendable Packet Vector]
    FlatSharp --> MapPkg[Map Package / Workshop Diff<br>Checkpoints + Deltas]
    
    FBS -.-> flatc[flatc CLI Tool]
    MapPkg -.-> flatc
    flatc -.-> JSON[Optional JSON Export<br>Human Introspection / Tooling]
```

---

## 1. Why FlatSharp / FlatBuffers?

1. **Zero-Copy & Direct Span Access**: FlatSharp reads directly from `ReadOnlyMemory<byte>` or `ReadOnlySpan<byte>` without allocating heap objects for intermediate tables or byte arrays.
2. **Flexible-Sized Vectors**: First-class, zero-copy support for `vector<ubyte>` (binary octree blobs) and `vector<Command>` (command streams).
3. **Schema Evolution & Backward Compatibility**: FlatBuffers supports adding new fields, deprecating old ones, and evolving packet structures without breaking older clients or saved maps.
4. **Unified Codebase**: Network packets, recorded demos, map files, and workshop diffs share identical schemas and types. No duplicate codecs (`WireCodec` vs `TextCodec` vs `CommandCodec`).
5. **No Repository Bloat, Optional Introspection**: Binary files are maintained in the repository/distribution. When human inspection or debugging is needed, the standard `flatc` compiler converts binary FlatBuffers to and from JSON instantly (`flatc --json --raw-binary schema.fbs -- map.bin`). JSON is strictly an introspection artifact and is **not** committed to version control.

---

## 2. Retiring Custom Merkle DAGs, `System.IO.Hashing`, and `XxHash`

In earlier iterations, the codebase introduced `System.IO.Hashing` and `XxHash128` to support an in-binary Git clone: every octree cube, vertex block, and commit node was hashed into a 128-bit `ObjectId` to build a content-addressed Merkle DAG (`MapObjectStore`).

By establishing that **map diffing only occurs between major `remip` keyframe snapshots and whole map packages**:
* **Fine-Grained Merkle Hashing is Obsolete**: We no longer track thousands of micro-`ObjectId` nodes per cube. The map state is governed by coarse-grained, high-density keyframes at remip boundaries.
* **Standard SHA-256 via Built-in BCL (`System.Security.Cryptography`)**:
  - SHA-256 is built directly into .NET (`System.Security.Cryptography.SHA256`) with zero third-party dependencies.
  - Modern .NET utilizes CPU hardware-accelerated instructions (Intel SHA-NI and ARMv8 Crypto on Apple Silicon).
  - A 32-byte SHA-256 `base_map_hash` provides full collision resistance against malicious or accidental collision when validating workshop downloads and multiplayer server parity.
* **Clean Dependency Removal**:
  - Remove `System.IO.Hashing` from `SauerbratenRevival.csproj`.
  - Delete `World/Runtime/Formats/Save/ObjectId.cs` and `World/Runtime/Formats/Save/MapObjectStore.cs`.

---

## 3. Detailed Specification: Keyframe Binary Format

A critical architectural question is how keyframe snapshots are physically encoded. An octree containing 200,000+ cubes cannot be stored as hundreds of thousands of individual FlatBuffer tables without incurring massive vtable/pointer overhead (4–8 bytes of overhead per node would double map file sizes).

Instead, we employ a **hybrid layout**:
* **Outer Structure (FlatSharp Table)**: Governs keyframe metadata, chunk indices, world scale, entity tables, and raw byte spans with full schema versioning and forward compatibility.
* **Inner Payload (`[ubyte]`)**: A dense, canonical pre-order traversal stream matching the high-efficiency memory blit of Sauerbraten's native geometry.

```mermaid
graph TD
    subgraph FlatSharp Container
        KP[KeyframePackage Table]
        KP --> Meta[Metadata: WorldScale, BoundingBox, KeyframeIndex]
        KP --> Chunks[Vector of ChunkKeyframe Tables]
        KP --> Ents[Vector of MapEntity Tables]
    end

    subgraph ChunkKeyframe Payload
        Chunks --> CB1[Chunk 0: vector ubyte]
        Chunks --> CB2[Chunk 1..7: vector ubyte]
    end

    subgraph Dense Binary Stream inside CB
        CB1 --> Stream[Sequential Pre-Order Traversal Stream<br>FlagByte + Faces + Textures + Optional SurfaceInfo]
    end
```

### A. FlatBuffers Keyframe Schema

```flatbuffers
namespace SauerbratenRevival.Format;

enum CompressionCodec : ubyte {
    None = 0,
    Zstd = 1,
    Lz4 = 2
}

table ChunkKeyframe {
    chunk_index: ubyte;              // 0..7 for the 8 primary world roots
    uncompressed_size: uint32;
    node_count: uint32;
    data: [ubyte];                   // Dense pre-order cube stream
}

table KeyframePackage {
    keyframe_index: uint32;          // Monotonically increasing milestone ID
    format_version: uint32 = 1;
    world_scale: int32;              // WorldSize = 1 << world_scale
    base_snapshot_hash: [ubyte];     // 32-byte SHA-256 of the immediate parent keyframe
    chunks: [ChunkKeyframe];         // Independent 8 root octants
    entities: [EntityCmd];           // Active map entities at this keyframe
}
```

### B. Dense Node Stream Layout (`data: [ubyte]`)

Within each `ChunkKeyframe.data`, cubes are serialized in a deterministic, depth-first pre-order traversal (children 0 through 7). Leaf cubes and branch cubes are differentiated by a single leading flag byte:

| Byte Offset | Field | Type | Description |
|:---|:---|:---|:---|
| `0` | `Flags` | `ubyte` | Bitmask defining optional features present on this cube |
| `1 .. 12` | `Faces` | `uint32[3]` | 12 edge/corner height offsets (fixed-point DMF coordinates) |
| `13 .. 24` | `Textures` | `uint16[6]` | VSlot texture IDs for each of the 6 cube faces |
| *Conditional* | `Material` | `uint16` | Present if `(Flags & FlagMaterial) != 0` (e.g. `MAT_WATER`, `MAT_LAVA`) |
| *Conditional* | `Merged` | `ubyte` | Present if `(Flags & FlagMerged) != 0` (merged face mask for remip) |
| *Conditional* | `Visible` | `ubyte` | Present if `(Flags & FlagVisible) != 0` (visible face mask for remip) |
| *Conditional* | `Surfaces` | Variable | Present if `(Flags & FlagSurface) != 0` (baked lightmap / smooth normal links) |
| *Conditional* | `Children` | Stream | If `(Flags & FlagChildren) != 0`, immediately followed by 8 child cube streams |

#### Flags Bitmask Definition:
* `Bit 0 (0x01) - FlagChildren`: Cube has 8 sub-cubes. If clear, cube is a leaf.
* `Bit 1 (0x02) - FlagMaterial`: Material is non-air (`Material != Octa.MAT_AIR`).
* `Bit 2 (0x04) - FlagMerged`: Remip merge bits are non-zero.
* `Bit 3 (0x08) - FlagVisible`: Remip visibility bits are non-zero.
* `Bit 4 (0x10) - FlagSurface`: Baked lightmap / surface extension is attached.

#### Surface Extension Layout (when `FlagSurface` is set):
Persists baked radiance/lightmap bindings that cannot be derived geometrically:
* `Surfaces`: $6 \times 4$ bytes = 24 bytes (each face: `lmid0: ubyte`, `lmid1: ubyte`, `verts: ubyte`, `numverts: ubyte`).
* `VertCount`: `uint16` ($N$).
* `Verts`: $N \times 12$ bytes (each vertex: `x: uint16`, `y: uint16`, `z: uint16`, `u: uint16`, `v: uint16`, `norm: uint16`).

### C. Performance & Density
* **Leaf Cube (common air or solid)**: Exactly **25 bytes** (`1 + 12 + 12`).
* **Material/Merged Cube**: **27–29 bytes**.
* **Deserialization Speed**: A single pass reader initializes nodes sequentially with zero heap allocations, walking directly through the FlatSharp `ReadOnlySpan<byte>`. 200,000 cubes deserialize in **under 8 milliseconds**.

---

## 4. The Remip Boundary & Keyframe Lifecycle

```mermaid
sequenceDiagram
    autonumber
    actor User as Editor / Game Action
    participant Sink as CommandSink
    participant Octa as Octa World Tree
    participant Key as Keyframe Manager
    participant Store as MapStore (FlatSharp)

    User->>Sink: Submit Edits (Face push, texture paint)
    Sink->>Octa: Apply mutations (in-memory commands)
    User->>Sink: Trigger Remip (OctaEdit.RemipAll)
    Sink->>Octa: Execute Remip (merge runs, optimize octree)
    Note over Sink,Octa: Geometry is now structurally unified & compacted
    Sink->>Key: CaptureKeyframe(Octa)
    Key->>Store: Serialize KeyframePackage (Chunk blobs + SHA-256 base hash)
    Key->>Sink: Squash & Archive pre-remip commands
    Note over Sink,Store: Active delta buffer reset to empty
```

1. **Deterministic Geometry Compaction**: `OctaEdit.Remip` merges coplanar leaf nodes and updates `Merged` / `Visible` masks.
2. **Keyframe Trigger**: Because `remip` is a lossy/derivation-only barrier, completing a `remip` automatically flags the world state for a keyframe capture.
3. **Squashing Pre-Remip History**: Commands submitted prior to the `remip` are retired from the active delta stream. The new keyframe becomes the new base ground truth.
4. **Instant Runtime Load**:
   $$\text{World at HEAD} = \text{LoadKeyframe}(\text{LatestKeyframe}) \xrightarrow{\text{replay}} \text{ApplyDeltas}(\text{PostRemipCommands})$$

---

## 5. Map Package Directory Layout

A map is stored as a clean directory of external resources, separating engine scene presentation from FlatSharp binary data:

```
res://resources/maps/<map_name>/
├── Level.tscn              # Godot Scene: Skybox, Sun yaw/pitch, fog, mode rules, sound triggers
├── manifest.tres           # Godot Resource: Material/vslot bindings & model paths (engine asset links)
├── map.bin                 # FlatSharp Package: Header, Keyframe Octree Blobs, Entities, Deltas
└── (optional) debug.json   # Generated on-demand via `flatc --json` (Git-ignored)
```

1. **`Level.tscn`**: Shipped Godot scene providing scene-tree nodes: `WorldEnvironment`, `DirectionalLight3D`, audio streams, spawn points, and game mode logic.
2. **`manifest.tres`**: Text resource for Godot engine asset references (texture paths, materials, GLTF model paths). This stays in `.tres` because Godot's editor needs to manage asset dependencies and `.uid` tracking directly.
3. **`map.bin`**: The authoritative binary map package compiled via FlatSharp. Contains the latest base keyframe, intermediate blobs, entities, and any pending delta commands.
4. **No Git-tracked `.tres` command logs**: Deleted entirely. Clean, fast binary loading replaces thousand-line command text logs.

---

## 6. Steam Workshop & Delta Diff Packages

The FlatSharp schema directly supports **delta diff packaging** for Steam Workshop uploads and downloads:

```mermaid
graph LR
    Base[Base Map / Empty Map<br>SHA-256 Hash] --> Diff[Workshop Diff Package<br>.fbs binary]
    Diff --> Applied[Materialized Client World]
    
    subgraph Diff [Workshop Diff Package]
        direction TB
        BHash[Expected Base Hash: SHA-256]
        Blobs[Intermediary Blobs / Remip Keyframes]
        Cmds[Delta Commands]
        MPatch[Manifest Patch]
    end
```

### How Workshop Packaging Works:
1. **Delta Packages**:
   * When modifying an existing map (e.g., `church51` or `empty_map`), the author does not need to distribute the entire base world asset collection.
   * The package includes:
     - `base_map_hash`: 32-byte SHA-256 hash of the parent map or base snapshot.
     - `keyframes`: Intermediary `ChunkKeyframe` blobs capturing only the chunks modified across `remip`s.
     - `deltas`: Vector of `WorldCommand`s applied on top of the latest keyframe.
     - `manifest_delta`: Texture and model additions.
2. **Standalone Compilation**:
   * For maps built from scratch or for clients without the base map, the editor compiles a **Standalone Map Package** containing the full keyframe blob and all assets.
3. **Bandwidth Efficiency**:
   * Workshop updates are tiny: changing a hallway and running `remip` produces a diff package of only a few kilobytes (the modified chunk blobs + delta commands).

---

## 7. Complete FlatBuffers Schema Specification (`schema.fbs`)

```flatbuffers
namespace SauerbratenRevival.Format;

enum WorldOp : ubyte {
    None = 0,
    DeleteCube = 1,
    EditFace = 2,
    SetFaceTexture = 3,
    ReplaceTexture = 4,
    SetFaceMaterial = 5,
    FlipCube = 6,
    RotateCube = 7,
    Remip = 20,
    NewMap = 30,
    AddEntity = 40,
    RemoveEntity = 41,
    EditEntity = 42,
    ReplaceBlock = 50
}

enum CompressionCodec : ubyte {
    None = 0,
    Zstd = 1,
    Lz4 = 2
}

struct Vec3i {
    x: int32;
    y: int32;
    z: int32;
}

struct Vec3f {
    x: float32;
    y: float32;
    z: float32;
}

struct SelectionBox {
    origin: Vec3i;
    size: Vec3i;
    grid: int32;
    orient: int32;
}

table EditFaceCmd {
    sel: SelectionBox;
    dir: int32;
    mode: int32;
    stride: int32;
}

table SetTextureCmd {
    sel: SelectionBox;
    texture_id: int32;
    flags: int32;
}

table DeleteCubeCmd {
    sel: SelectionBox;
}

table RemipCmd {
    mip_vis: bool;
}

table EntityCmd {
    entity_id: uint64;
    type: int32;
    pos: Vec3f;
    attr1: int32;
    attr2: int32;
    attr3: int32;
    attr4: int32;
    attr5: int32;
}

union CommandPayload {
    EditFaceCmd,
    SetTextureCmd,
    DeleteCubeCmd,
    RemipCmd,
    EntityCmd
}

table WorldCommand {
    sequence: uint64;
    peer_id: int32;
    timestamp: uint32;
    op: WorldOp;
    payload: CommandPayload;
}

table ChunkKeyframe {
    chunk_index: ubyte;              // 0..7
    uncompressed_size: uint32;
    node_count: uint32;
    data: [ubyte];                   // Dense pre-order cube stream
}

table KeyframePackage {
    keyframe_index: uint32;
    format_version: uint32 = 1;
    world_scale: int32;
    base_snapshot_hash: [ubyte];     // 32-byte SHA-256
    chunks: [ChunkKeyframe];
    entities: [EntityCmd];
}

table MapPackage {
    format_version: uint32 = 1;
    map_name: string;
    author: string;
    compression: CompressionCodec;   // Archive-wide compression (None, Zstd, Lz4)
    base_map_hash: [ubyte];          // 32-byte SHA-256 if diff package, empty if standalone
    
    keyframes: [KeyframePackage];    // Intermediary snapshots taken at remip / milestones
    deltas: [WorldCommand];          // Commands applied since the latest keyframe
    entities: [EntityCmd];           // Active world entities
}

table NetworkPacket {
    packet_id: uint32;
    commands: [WorldCommand];
}

root_type MapPackage;
```

---

## 8. Migration & Implementation Strategy

1. **Add `FlatSharp` Package & Prune `System.IO.Hashing`**:
   * Add `FlatSharp.Runtime` and `FlatSharp.Compiler` via NuGet to `SauerbratenRevival.csproj`.
   * Remove `System.IO.Hashing` from `SauerbratenRevival.csproj`.
   * Delete `ObjectId.cs` and `MapObjectStore.cs`.
2. **Retire Manual Bit-Packing**:
   * Deprecate manual integer-packing in `CommandCodec.cs` / `WireCodec.cs`.
   * Use FlatSharp generated classes for live ENet networking and command logs.
3. **Implement Keyframe Capture in `OctaEdit.Remip`**:
   * When `RemipAll` executes, invoke keyframe generation to serialize the 8 root chunks into `ChunkKeyframe` payloads.
   * Clear active transaction deltas in `CommandSink`.
4. **Fast Level Loading**:
   * `MapStore.LoadWorld()`: Reads `map.bin`, reads the latest `KeyframePackage`, decodes chunk spans into `Cube` arrays in $O(\text{nodes})$, and replays trailing deltas.
5. **Tooling & Inspection**:
   * Provide an engine CLI flag (`--dump-json <map.bin>`) calling `flatc` to convert binary packages to readable JSON for debugging.

---

## Consequences

### Positive
* **Unified Pipeline**: One schema (`schema.fbs`) handles wire transmission, recorded demos, map storage, and Steam Workshop diffs.
* **Solves the Remip Wall**: Keyframe snapshots at remip boundaries eliminate the need to replay thousands of obsolete pre-remip micro-operations.
* **Lean Dependencies**: Deletes `System.IO.Hashing`, `ObjectId.cs`, and `MapObjectStore.cs`. Standard SHA-256 handles all map hashing with hardware acceleration.
* **High Density & Speed**: The hybrid container + pre-order stream achieves ~25–29 bytes per cube and sub-10ms deserialization.
* **Workshop-Ready**: Diff packages depend solely on a 32-byte SHA-256 `base_map_hash` and intermediate chunk keyframes.
* **Zero Repo Bloat**: Git tracks clean binary packages without millions of lines of unmergeable `.tres` text logs.

### Trade-offs
* **Binary Files in Git**: Map packages (`map.bin`) are binary. However, because they store compact keyframes and squashed deltas, file sizes remain tiny (tens to hundreds of kilobytes).
* **FlatSharp Schema Build Step**: Adds `FlatSharp` NuGet dependency and `.fbs` code generation to the build pipeline.

---

## Key Source Files

- `World/Schema/schema.fbs` — Authoritative FlatBuffers schema definition.
- `World/Commands/Command.cs` — In-engine command definitions and interfaces.
- `World/Runtime/Formats/Save/MapStore.cs` — Map package loading, keyframe extraction, and delta application.
- `World/Runtime/Formats/Save/KeyframePackage.cs` — High-level wrapper over FlatSharp keyframe tables.
- `World/Network/NetMessage.cs` — Network transmission using FlatSharp spans over ENet.
