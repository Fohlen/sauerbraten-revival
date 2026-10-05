using System;
using System.IO;
using Godot;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.World.Ogz;

/// <summary>
/// Dev-only inspection scene: reads a legacy <c>.ogz</c> from the reference dump,
/// builds the runtime octree and displays the chunked geometry.
///
/// This deliberately reads OGZ at runtime for tooling convenience — it is NOT the
/// shipping path. The shipped importer converts maps to the native format; see
/// docs/ogz-import.md.
///
/// Run <c>scenes/map_viewer.tscn</c>, set <see cref="OgzPath"/> in the inspector
/// (or rely on the <c>SAUER_REF</c> default), then F6. Geometry is untextured.
/// </summary>
[GlobalClass]
public partial class OgzMapViewer : Node3D
{
    [Export] public string OgzPath { get; set; } = "";
    [Export] public int ChunkSize { get; set; } = 512;

    private static readonly Color MeshTint = new(0.8f, 0.8f, 0.85f);

    public override void _Ready()
    {
        string path = string.IsNullOrEmpty(OgzPath) ? DefaultPath() : OgzPath;
        if (!File.Exists(path))
        {
            GD.PushError($"[viewer] OGZ not found: '{path}'. Set OgzPath or SAUER_REF.");
            return;
        }

        var map = OgzReader.Read(path);
        var octa = map.ToOcta();
        var chunked = new OctaMesher(octa).BuildChunked(ChunkSize);

        var material = new StandardMaterial3D { AlbedoColor = MeshTint };
        var world = new Node3D { Name = "World", RotationDegrees = new Vector3(-90, 0, 0) }; // Z-up -> Y-up
        AddChild(world);

        Aabb bounds = default;
        bool hasBounds = false;
        foreach (var chunk in chunked.Chunks)
        {
            world.AddChild(new MeshInstance3D
            {
                Name = $"Chunk_{chunk.Origin.X}_{chunk.Origin.Y}_{chunk.Origin.Z}",
                Mesh = chunk.Mesh,
                Position = new Vector3(chunk.Origin.X, chunk.Origin.Y, chunk.Origin.Z),
                MaterialOverride = material,
            });

            var aabb = chunk.Mesh.GetAabb();
            aabb.Position += new Vector3(chunk.Origin.X, chunk.Origin.Y, chunk.Origin.Z);
            bounds = hasBounds ? bounds.Merge(aabb) : aabb;
            hasBounds = true;
        }

        AddSkyAndCamera(hasBounds ? bounds : new Aabb(Vector3.Zero, Vector3.One));
        GD.Print($"[viewer] {path}: chunks={chunked.Chunks.Count} triangles={chunked.TriangleCount}");
    }

    private static string DefaultPath()
    {
        string root = System.Environment.GetEnvironmentVariable("SAUER_REF")
                      ?? "/Users/fohlen/Downloads/sauerbraten-code-r6889";
        return Path.Combine(root, "packages", "base", "church51.ogz");
    }

    private void AddSkyAndCamera(Aabb sauerBounds)
    {
        AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-50, -30, 0),
            ShadowEnabled = true,
        });

        // Sauerbraten bounds are Z-up; the World node rotates them to Godot Y-up.
        Vector3 sauerCenter = sauerBounds.GetCenter();
        Vector3 sauerSize = sauerBounds.Size;
        Vector3 centre = new(sauerCenter.X, sauerCenter.Z, -sauerCenter.Y);
        float radius = Mathf.Max(sauerSize.Length() * 0.5f, 1.0f);

        var camera = new Camera3D { Name = "Camera3D", Fov = 75 };
        camera.Position = centre + new Vector3(0, radius * 0.6f, radius * 1.3f);
        AddChild(camera);
        camera.LookAt(centre, Vector3.Up);
        camera.Current = true;
    }
}
