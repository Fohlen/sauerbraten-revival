using System.Collections.Generic;
using System.Linq;
using Godot;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Meshing;
using SauerbratenRevival.World.Models;

namespace SauerbratenRevival.World;

/// <summary>
/// Universal interface for a map world provider (legacy OGZ, native saved map, or empty seed world).
/// Exposes the runtime octree authority, command sink, rendering, and gameplay entities.
/// </summary>
public interface IMapSource
{
    /// <summary>The runtime octree authority (Z-up coordinate space).</summary>
    Octa? Octa { get; set; }

    /// <summary>The Godot Node3D hosting the world in the scene tree (rotated Z-up -> Y-up).</summary>
    Node3D WorldNode { get; }

    /// <summary>Command sink for submitting and undoing geometry/entity edits.</summary>
    WorldCommandSink? Sink { get; set; }

    /// <summary>Chunked mesh renderer, supporting dynamic dirty-chunk invalidation.</summary>
    WorldChunkRenderer? Renderer { get; set; }

    /// <summary>Runtime radiance volume for lighting, if baked/computed.</summary>
    Lighting.RadianceVolume? Radiance { get; }

    /// <summary>
    /// The shared octree physics authority driving this map's simulation, when it
    /// has one. A player must be stepped through the same instance it was set up
    /// with, so scenes should reuse this rather than building another.
    /// </summary>
    Physics.OctaPhysics? Physics => null;

    /// <summary>Texture resolver for world and entity materials.</summary>
    ITextureSource? Textures { get; }

    /// <summary>Model catalogue for mapmodels, items, and player bodies.</summary>
    IModelCatalog? Models { get; }

    /// <summary>AABB of the geometry in Sauerbraten (Z-up, local) space.</summary>
    Aabb Bounds => Octa != null
        ? new Aabb(Vector3.Zero, new Vector3(Octa.WorldSize, Octa.WorldSize, Octa.WorldSize))
        : default;

    /// <summary>Calculates camera framing center in Godot space from Sauerbraten bounds.</summary>
    public static Vector3 ComputeCentre(Aabb bounds) => SauerbratenToGodot(bounds.GetCenter());

    /// <summary>Calculates camera framing radius from Sauerbraten bounds.</summary>
    public static float ComputeRadius(Aabb bounds) => Mathf.Max(bounds.Size.Length() * 0.5f, 1.0f);

    /// <summary>World bounding center in Godot space for cameras/framing.</summary>
    Vector3 Centre => ComputeCentre(Bounds);

    /// <summary>World bounding radius in Godot units.</summary>
    float Radius => ComputeRadius(Bounds);

    /// <summary>Whether the world octree and geometry have been materialized.</summary>
    bool Built { get; }

    /// <summary>Materialize the octree, geometry, and entities.</summary>
    void Build();

    /// <summary>Finds a player spawn point in Sauerbraten coordinates, falling back to map center.</summary>
    (Vector3 Position, float Yaw) FindSpawn(int index = 0) => (Bounds.GetCenter(), 0f);

    /// <summary>Map item manager, if supported by the world.</summary>
    ItemManager? Items { get; }

    /// <summary>Flag manager for CTF / Protect / Hold modes, if supported.</summary>
    Game.FlagManager? Flags { get; }

    /// <summary>Jump pads present in the map.</summary>
    IReadOnlyList<JumpPad> JumpPads { get; }

    /// <summary>Teleports present in the map.</summary>
    IReadOnlyList<Teleport> Teleports { get; }

    /// <summary>Active weapon effects renderer, if attached.</summary>
    Effects.WeaponEffectsRenderer? WeaponEffects { get; set; }

    /// <summary>Shared simulation clock for physics/match time.</summary>
    SimClock? Clock { get; }

    /// <summary>Registers an active player for simulation stepping.</summary>
    void RegisterPlayer(Ogz.MapPlayer player) { }

    /// <summary>Unregisters an active player from simulation stepping.</summary>
    void UnregisterPlayer(Ogz.MapPlayer player) { }

    /// <summary>Converts a position from Sauerbraten Z-up coordinates to Godot Y-up coordinates under standard world rotation.</summary>
    public static Vector3 SauerbratenToGodot(Vector3 sauer) => new(sauer.X, sauer.Z, -sauer.Y);

    /// <summary>Converts a direction or position from Godot Y-up coordinates to Sauerbraten Z-up coordinates.</summary>
    public static Vector3 GodotToSauerbraten(Vector3 godot) => new(godot.X, -godot.Z, godot.Y);

    /// <summary>Helper resolving the N-th spawn point from a sequence, falling back to bounds center.</summary>
    public static (Vector3 Position, float Yaw) ResolveSpawn(IEnumerable<(Vector3 Pos, float Yaw)> spawns, Vector3 fallback, int index = 0)
    {
        int current = 0;
        foreach (var (pos, yaw) in spawns)
        {
            if (current++ == index) return (pos, yaw);
        }
        return (fallback, 0f);
    }
}
