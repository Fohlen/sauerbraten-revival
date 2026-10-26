using Chickensoft.GoDotTest;
using Godot;
using System.Linq;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Editor;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Runtime.Formats.Ogz;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Runtime;
using MapPlayer = SauerbratenRevival.World.Game.Players.MapPlayer;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies editor scenes (<c>scenes/editor_scene.tscn</c> and <c>scenes/empty_map.tscn</c>)
/// instantiate a <see cref="MapPlayer"/> with edit mode enabled, that an
/// <see cref="EditorController"/> is automatically wired to the underlying <see cref="IMapSource"/>,
/// and that the play scene launched in the front-end's <c>coop_edit</c> mode does the same.
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

        TestAssert.Equal(0, map.Renderer.GetChildCount(), "chunk renderer bypasses scene tree nodes");
        TestAssert.True(map.Renderer.ChunkCount > 0, "at least one chunk exists");
        var origin = map.Renderer.ChunkOrigins.First();
        Rid meshRid = map.Renderer.GetChunkMeshRid(origin);
        TestAssert.True(meshRid.IsValid, "chunk has a valid RenderingServer mesh RID");
        Rid materialRid = RenderingServer.MeshSurfaceGetMaterial(meshRid, 0);
        TestAssert.True(materialRid.IsValid, "chunk surface uses the radiance shader material RID");

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

    /// <summary>
    /// The play scene launched with the front-end's EDIT objective: the menu emits
    /// the <c>coop_edit</c> mode and <see cref="Main"/> maps that mode's
    /// <see cref="ModeFlags.Edit"/> flag onto the viewer's edit entry point, so the
    /// player spawns floating with a live editor + edit HUD (not a match player).
    /// </summary>
    [Test]
    public void CoopEditModeLaunchesTheEditor()
    {
        var scene = GD.Load<PackedScene>("res://scenes/player_scene.tscn");
        TestAssert.True(scene != null, "player_scene.tscn loaded");

        var root = scene!.Instantiate<OgzMapViewer>();
        root.OgzPath = TestData.OgzPath;
        root.GameMode = "coop_edit";
        // Mirrors Main.StartMatch: the mode's Edit flag selects edit mode.
        root.EditMode = GameModeCatalog.TryGet(root.GameMode)?.Has(ModeFlags.Edit) ?? false;
        TestAssert.True(root.EditMode, "coop_edit carries ModeFlags.Edit");

        TestScene.AddChild(root);

        MapPlayer? player = null;
        EditorController? editor = null;
        foreach (var child in root.MapSource!.WorldNode.GetChildren())
        {
            if (child is MapPlayer p) player = p;
            if (child is EditorController ec) editor = ec;
        }

        TestAssert.True(player != null, "player spawned under world");
        TestAssert.True(player!.EditMode, "player is in edit mode");
        TestAssert.True(player.Floating, "edit player floats instead of walking");
        TestAssert.True(editor != null, "editor controller attached under world");
        TestAssert.Equal(root.MapSource.Octa, editor!.Octa, "editor bound to map octa");
        TestAssert.True(editor.Hud != null, "editor drives the edit HUD");

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
