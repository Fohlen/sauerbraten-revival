using Chickensoft.GoDotTest;
using Godot;
using System.Linq;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Editor;
using SauerbratenRevival.World.Ogz;
using SauerbratenRevival.World.Physics;
using MapPlayer = SauerbratenRevival.World.Game.Players.MapPlayer;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies editor scenes (<c>scenes/editor_scene.tscn</c> and <c>scenes/empty_map.tscn</c>)
/// instantiate a <see cref="MapPlayer"/> with edit mode enabled, and that an
/// <see cref="EditorController"/> is automatically wired to the underlying <see cref="IMapSource"/>.
/// </summary>
public class EditorViewSceneTest : GoTestBase
{
    public EditorViewSceneTest(Node testScene) : base(testScene) { }

    [Test]
    public void PlayerEditModeSuppressesWeaponsAndFloats()
    {
        var player = new MapPlayer();
        player.Setup(new OctaPhysics(new Octa(8)), Vector3.Zero, 0f);

        TestAssert.False(player.EditMode, "not in edit mode initially");
        TestAssert.False(player.Floating, "not floating initially");

        player.EditMode = true;
        TestAssert.True(player.EditMode, "in edit mode");
        TestAssert.True(player.Floating, "floating in edit mode");
        TestAssert.Equal(PhysEnt.CsEditing, player.Entity.State, "CsEditing state");
        TestAssert.Equal("edit", player.SelectBodyAnim(), "body animation is edit");

        // Click while floating should not trigger attacks
        player._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        TestAssert.False(player.Combat.Attacking, "left click does not attack while in edit mode");

        player.Free();
    }

    [Test]
    public void EmptyMapStepsRegisteredPlayer()
    {
        var map = new EmptyMap { WorldScale = 10, ChunkSize = 128 };
        map.Build();
        TestAssert.True(map.Physics != null, "empty map physics initialized");

        int startMillis = map.Clock!.NowMillis;
        var player = new MapPlayer();
        map.RegisterPlayer(player);
        player.Setup(map.Physics!, ((IMapSource)map).FindSpawn().Position, 0f);
        player.EditMode = true; // floating, as the editor scene runs it

        TestAssert.True(map.Players.Contains(player), "player registered with empty map");
        TestAssert.Equal(startMillis, player.LastMillis, "player reads the empty map clock");

        Vector3 start = player.Entity.O;
        Input.ActionPress("move_forward");
        for (int i = 0; i < 10; i++) map._PhysicsProcess(0.010); // 2 substeps each -> 100 ms
        Input.ActionRelease("move_forward");

        TestAssert.Equal(startMillis + 100, map.Clock!.NowMillis, "empty map clock advanced by 100 ms");
        TestAssert.True(player.Entity.O.DistanceTo(start) > 1f, "forward input moves the player through empty map physics");

        map.UnregisterPlayer(player);
        player.Free();
        map.Free();
    }

    [Test]
    public void EmptyMapBakesSunlightRadiance()
    {
        var map = new EmptyMap { WorldScale = 10, ChunkSize = 128, BakeRadiance = true };
        map.Build();

        TestAssert.True(map.Radiance != null, "empty map radiance volume baked");
        TestAssert.True(map.Radiance!.Texture != null, "radiance 3D texture created");
        TestAssert.True(ReferenceEquals(map.Radiance, map.Renderer!.Radiance), "renderer ticks the baked volume for rebakes");

        bool anyDirectional = map.Radiance.Grid.Probes.Any(p => p.Dir.LengthSquared() > 0f);
        TestAssert.True(anyDirectional, "sun/sky bake produced directional irradiance");

        var chunk = map.Renderer.GetChildren().OfType<MeshInstance3D>().FirstOrDefault();
        TestAssert.True(chunk != null, "a chunk mesh exists");
        TestAssert.True(chunk!.Mesh.SurfaceGetMaterial(0) is ShaderMaterial, "chunk uses the radiance shader material");

        map.Free();
    }

    [Test]
    public void EditorSceneLoadsWithEditPlayerAndController()
    {
        var scene = GD.Load<PackedScene>("res://scenes/editor_scene.tscn");
        TestAssert.True(scene != null, "editor_scene.tscn loaded");

        var root = scene!.Instantiate<OgzMapViewer>();
        TestAssert.True(root.EditMode, "viewer EditMode is true");
        TestAssert.True(root.SpawnPlayer, "viewer SpawnPlayer is true");

        // Adding to scene tree triggers _Ready
        TestScene.AddChild(root);

        TestAssert.True(root.MapSource != null, "map source initialized");
        TestAssert.True(root.MapSource!.Built, "map source built");

        MapPlayer? player = null;
        EditorController? editor = null;
        foreach (var child in root.MapSource.WorldNode.GetChildren())
        {
            if (child is MapPlayer p) player = p;
            if (child is EditorController ec) editor = ec;
        }

        TestAssert.True(player != null, "player spawned under world");
        TestAssert.True(player!.EditMode, "player is in edit mode");
        TestAssert.True(editor != null, "editor controller attached under world");
        TestAssert.Equal(root.MapSource.Octa, editor!.Octa, "editor controller bound to map octa");
        TestAssert.Equal(root.MapSource.Sink, editor.Sink, "editor controller bound to map sink");

        root.QueueFree();
    }

    [Test]
    public void EmptyMapSceneLoadsWithEditPlayerAndController()
    {
        var scene = GD.Load<PackedScene>("res://scenes/empty_map.tscn");
        TestAssert.True(scene != null, "empty_map.tscn loaded");

        var root = scene!.Instantiate<OgzMapViewer>();
        TestAssert.True(root.EditMode, "viewer EditMode is true");
        TestAssert.True(root.SpawnPlayer, "viewer SpawnPlayer is true");

        TestScene.AddChild(root);

        TestAssert.True(root.MapSource != null, "map source initialized");
        TestAssert.True(root.MapSource is EmptyMap, "map source is EmptyMap");
        TestAssert.True(root.MapSource!.Built, "empty map built");

        MapPlayer? player = null;
        EditorController? editor = null;
        foreach (var child in root.MapSource.WorldNode.GetChildren())
        {
            if (child is MapPlayer p) player = p;
            if (child is EditorController ec) editor = ec;
        }

        TestAssert.True(player != null, "player spawned under empty map");
        TestAssert.True(player!.EditMode, "player is in edit mode");
        TestAssert.True(editor != null, "editor controller attached under empty map");
        TestAssert.Equal(root.MapSource.Octa, editor!.Octa, "editor controller bound to empty map octa");

        root.QueueFree();
    }
}
