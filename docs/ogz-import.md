# OGZ Import Pipeline

One-time import of legacy `.ogz` maps into Sauerbraten Revival's native format.
This is an offline tool, not a runtime feature.

## Goals

1. Import hand-picked classic maps from the original game.
2. Extract all geometry, entities, textures, and lighting.
3. Produce our native format: octree `Resource` + `Level.tscn`.
4. **Never load `.ogz` at runtime** — the importer is a build-time tool.

---

## Architecture overview

```
┌─────────────────────────────────────────────────────────────────┐
│                    OGZ Import Pipeline                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌──────────┐    ┌──────────────┐    ┌───────────────────────┐ │
│  │  .ogz    │───▶│  OGZ Parser  │───▶│  In-memory octree     │ │
│  │  file    │    │  (C#)        │    │  + entities           │ │
│  └──────────┘    └──────────────┘    └───────────┬───────────┘ │
│                                                  │             │
│  ┌──────────┐                                    │             │
│  │ metadata │────────────────────────────────────┤             │
│  │  .json   │    (texture slots, mapmodels,      │             │
│  └──────────┘     lights, entity defs)           │             │
│                                                  ▼             │
│                                      ┌───────────────────────┐ │
│                                      │  Native format        │ │
│                                      │  - world.res (octree) │ │
│                                      │  - Level.tscn         │ │
│                                      │  - materials/         │ │
│                                      └───────────────────────┘ │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## Two-source approach

The importer needs information from two sources:

### 1. OGZ file (binary octree + entities)

The `.ogz` file contains:
- Octree structure (nodes, children, edges, faces).
- Per-face surface info (texture index, lightmap ID, vertex info).
- Entity list (position, type, attributes).
- Map header (worldsize, version, etc.).

This is parsed directly in C# — the format is documented and stable.

### 2. Metadata JSON (texture/mapmodel resolution)

The `.ogz` file stores **indices**, not paths:
- Face texture → `vslots[index]` → `slots[slot.index]` → texture path.
- Mapmodel entity → `mapmodels[attr1]` → model path.
- Lights, sounds, particles → need attribute interpretation.

This metadata is **not** in the `.ogz` file — it's set by the map's `.cfg`
CubeScript, which executes in the original engine.

**Solution**: A one-time CubeScript patch that exports all resolved metadata to
JSON when a map loads in the original client.

---

## Metadata export patch

A small patch to the original Sauerbraten client that, on map load, writes:

```json
{
  "mapname": "church51",
  "worldsize": 1024,
  "slots": [
    {
      "index": 0,
      "textures": [
        { "type": "diffuse", "path": "textures/nieb/metal01.jpg" },
        { "type": "normal", "path": "textures/nieb/metal01_n.jpg" }
      ],
      "shader": "bumpworld",
      "params": { ... }
    },
    ...
  ],
  "vslots": [
    {
      "index": 0,
      "slot": 0,
      "scale": 1.0,
      "rotation": 0,
      "offset": [0, 0],
      "colorscale": [1, 1, 1]
    },
    ...
  ],
  "mapmodels": [
    { "index": 0, "path": "mapmodels/carrot/tree/tree01" },
    ...
  ],
  "entities": [
    {
      "index": 0,
      "type": "light",
      "position": [512, 512, 128],
      "attr1": 200,  // radius
      "attr2": 255,  // r
      "attr3": 240,  // g
      "attr4": 220   // b
    },
    ...
  ],
  "sounds": [
    { "index": 0, "path": "ambience/rain.ogg" },
    ...
  ]
}
```

This patch is applied once to a copy of the original source, the modified client
is run on each map, and the JSON output is committed to the repository.

**No ongoing maintenance** — the original game is frozen; we generate metadata
once for all maps we want to import.

---

## OGZ parser (C#)

A standalone C# parser that reads the binary `.ogz` format:

```csharp
public class OgzParser
{
    public OgzMap Parse(string path)
    {
        using var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new BinaryReader(stream);
        
        var header = ReadHeader(reader);
        var octree = ReadOctree(reader, header.WorldSize);
        var entities = ReadEntities(reader, header.NumEnts);
        
        return new OgzMap(header, octree, entities);
    }
}
```

Key structures to parse:
- **Header**: magic, version, headersize, worldsize, numents, etc.
- **Octree nodes**: recursive, 8 children per node, edge/face data.
- **Entities**: position (ivec), type, attr1-5.

The format is complex but documented in `worldio.cpp`. We port the loading logic.

---

## Import process

```
1. Load metadata JSON for the map.
2. Parse .ogz file → in-memory octree + raw entities.
3. Resolve texture indices → material paths via metadata.
4. Convert octree to our native format (compact binary Resource).
5. Convert entities to Godot nodes:
   - ET_LIGHT → OmniLight3D / SpotLight3D
   - ET_PLAYERSTART → SpawnPoint (custom node)
   - ET_MAPMODEL → MeshInstance3D (load .obj/.md3)
   - ET_SOUND → AudioStreamPlayer3D
   - etc.
6. Build material manifest (index → ShaderMaterial).
7. Save:
   - world.res (octree Resource)
   - Level.tscn (scene with entities + world reference)
   - materials/ (imported materials)
```

---

## Native format (new maps)

New maps created in the editor use a **compact binary octree format** — not OGZ.

| Aspect | OGZ (legacy) | Native format |
| --- | --- | --- |
| Compression | gzip | Godot binary Resource (optionally compressed) |
| Texture refs | indices → CubeScript | indices → material manifest |
| Entities | inline, attr1-5 | Godot nodes in scene tree |
| Lightmaps | embedded | separate or baked via LightmapGI |
| Versioning | implicit | explicit version field |

The native format is:
- **Simpler**: no CubeScript dependency.
- **Godot-native**: loads via `ResourceLoader`.
- **Editable**: round-trips through the editor without loss.

---

## Lightmap handling

Options for imported lightmaps:

1. **Convert to baked textures**: Extract Sauer's packed lightmaps, assign to
   faces as a secondary UV channel.
2. **Rebake with LightmapGI**: Place lights from entity data, run Godot's bake.
3. **Dynamic lighting only**: Skip lightmaps for MVP; use real-time lights.

Recommendation: Start with (3) for MVP, add (1) or (2) for visual fidelity later.

---

## Entity type mapping

| Sauer type | ID | Godot equivalent |
| --- | --- | --- |
| ET_LIGHT | 1 | OmniLight3D / SpotLight3D |
| ET_MAPMODEL | 2 | MeshInstance3D |
| ET_PLAYERSTART | 3 | SpawnPoint (custom) |
| ET_ENVMAP | 4 | ReflectionProbe |
| ET_PARTICLES | 5 | GPUParticles3D |
| ET_SOUND | 6 | AudioStreamPlayer3D |
| ET_SPOTLIGHT | 7 | SpotLight3D |
| Game-specific | ≥8 | Custom nodes (flags, pickups, etc.) |

---

## File locations

```
tools/ogz-import/
├── OgzParser.cs           # Binary parser
├── MetadataLoader.cs      # JSON metadata loader
├── OctreeConverter.cs     # OGZ octree → native format
├── EntityConverter.cs     # Entities → Godot nodes
├── MaterialBuilder.cs     # Build material manifest
└── ImportPipeline.cs      # Orchestrates the import

data/metadata/
├── church51.json          # Generated metadata per map
├── dust2.json
└── ...

assets/maps/
├── church51/
│   ├── world.res          # Native octree
│   ├── Level.tscn         # Scene with entities
│   └── materials/         # Imported materials
└── ...
```

---

## Summary

1. **Metadata patch**: One-time CubeScript export → JSON for all target maps.
2. **OGZ parser**: C# binary parser for octree + entities.
3. **Importer**: Combines OGZ + metadata → native format + Godot scene.
4. **Native format**: New maps use compact binary, not OGZ.
5. **No runtime OGZ**: The importer is a build-time tool only.

This approach:
- Avoids maintaining a CubeScript interpreter.
- Decouples from the original engine at runtime.
- Allows importing any map with a one-time metadata export.
- Produces clean Godot-native output.
