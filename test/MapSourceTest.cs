using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Editor;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Runtime.Formats.Ogz;
using SauerbratenRevival.World.Runtime;
using SauerbratenRevival.World.Runtime.Formats.Save;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies that all map world providers (<see cref="MapWorld"/>, <see cref="EmptyMap"/>,
/// and <see cref="MapLevel"/>) correctly implement the <see cref="IMapSource"/> interface.
/// </summary>
public class MapSourceTest : GoTestBase
{
    public MapSourceTest(Node testScene) : base(testScene) { }

    [Test]
    public void EmptyMapImplementsMapSource()
    {
        var emptyMap = new EmptyMap { WorldScale = 10, ChunkSize = 128 };
        IMapSource source = emptyMap;

        TestAssert.False(source.Built, "not built initially");
        source.Build();
        TestAssert.True(source.Built, "built after Build()");
        TestAssert.True(source.Octa != null, "octa present");
        TestAssert.True(source.Sink != null, "sink present");
        TestAssert.True(source.Renderer != null, "renderer present");
        TestAssert.Equal(emptyMap, source.WorldNode, "world node is self");

        var (pos, yaw) = source.FindSpawn();
        TestAssert.Equal(new Vector3(512, 512, 512), pos, "empty map spawn at center");
        TestAssert.Equal(0f, yaw, "empty map spawn yaw");

        var controller = new EditorController();
        controller.BindMapSource(source);
        TestAssert.Equal(source.Octa, controller.Octa, "editor controller bound octa");
        TestAssert.Equal(source.Sink, controller.Sink, "editor controller bound sink");
        TestAssert.Equal(source.Renderer, controller.Renderer, "editor controller bound renderer");

        controller.Free();
        emptyMap.Free();
    }

    [Test]
    public void MapWorldImplementsMapSource()
    {
        var mapWorld = new MapWorld { OgzPath = "base/church51.ogz" };
        IMapSource source = mapWorld;

        TestAssert.False(source.Built, "not built initially");
        source.Build();
        TestAssert.True(source.Built, "built after Build()");
        TestAssert.True(source.Octa != null, "octa present");
        TestAssert.True(source.Sink != null, "sink present");
        TestAssert.True(source.Renderer != null, "renderer present");
        TestAssert.Equal(mapWorld, source.WorldNode, "world node is self");
        TestAssert.True(source.Radius > 0f, "valid radius");

        var (pos, _) = source.FindSpawn();
        TestAssert.True(pos != Vector3.Zero, "spawn point resolved");

        var controller = new EditorController();
        controller.BindMapSource(source);
        TestAssert.Equal(source.Octa, controller.Octa, "editor controller bound octa");
        TestAssert.Equal(source.Sink, controller.Sink, "editor controller bound sink");
        TestAssert.Equal(source.Renderer, controller.Renderer, "editor controller bound renderer");

        controller.Free();
        mapWorld.Free();
    }

    [Test]
    public void MapLevelImplementsMapSource()
    {
        string dir = Path.Combine(OS.GetUserDataDir(), "map_level_test_" + System.Guid.NewGuid().ToString("N"));
        var octa = new Octa(8);
        octa.EmptyMap(8);
        MapStore.SaveCompacted(dir, octa);

        var mapLevel = new MapLevel { MapDir = dir, ChunkSize = 64 };
        IMapSource source = mapLevel;

        TestAssert.False(source.Built, "not built initially");
        source.Build();
        TestAssert.True(source.Built, "built after Build()");
        TestAssert.True(source.Octa != null, "octa present");
        TestAssert.True(source.Sink != null, "sink present");
        TestAssert.True(source.Renderer != null, "renderer present");
        TestAssert.Equal(mapLevel, source.WorldNode, "world node is self");

        var (pos, _) = source.FindSpawn();
        TestAssert.Equal(new Vector3(512, 512, 512), pos, "spawn point at center");

        var controller = new EditorController();
        controller.BindMapSource(source);
        TestAssert.Equal(source.Octa, controller.Octa, "editor controller bound octa");
        TestAssert.Equal(source.Sink, controller.Sink, "editor controller bound sink");
        TestAssert.Equal(source.Renderer, controller.Renderer, "editor controller bound renderer");

        controller.Free();
        mapLevel.Free();
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    [Test]
    public void GameWorldsSupportSimulationAndProjectiles()
    {
        var emptyMap = new EmptyMap { WorldScale = 10, ChunkSize = 128 };
        emptyMap.Build();
        IGameWorld world = emptyMap;

        TestAssert.True(world.ActiveRockets != null, "active rockets list present");
        TestAssert.True(world.ActiveBouncers != null, "active bouncers list present");
        TestAssert.True(world.Players != null, "players list present");

        var player = new MapPlayer();
        world.RegisterPlayer(player);
        TestAssert.Equal(1, world.Players!.Count, "player registered");
        TestAssert.Equal(player, world.Players[0], "registered player is player");

        world.UnregisterPlayer(player);
        TestAssert.Equal(0, world.Players.Count, "player unregistered");

        player.Free();
        emptyMap.Free();
    }
}
