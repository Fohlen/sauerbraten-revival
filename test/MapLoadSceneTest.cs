using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.Tests;

public class MapLoadSceneTest : GoTestBase
{
    public MapLoadSceneTest(Node testScene) : base(testScene) { }

    [Test]
    public void MapLoadSceneInstantiatesCorrectly()
    {
        var scene = GD.Load<PackedScene>("res://scenes/map_load.tscn");
        TestAssert.True(scene != null, "map_load.tscn loaded");

        var mapLoad = scene!.Instantiate<MapLoad>();
        TestScene.AddChild(mapLoad);

        try
        {
            TestAssert.False(mapLoad.Visible, "MapLoad initially hidden");
            TestAssert.True(mapLoad.ProgressBar != null, "ProgressBar exists");
            TestAssert.True(mapLoad.StatusLabel != null, "StatusLabel exists");
            TestAssert.True(mapLoad.TitleLabel != null, "TitleLabel exists");
            TestAssert.True(mapLoad.InfoLabel != null, "InfoLabel exists");
            TestAssert.True(mapLoad.MapshotRect != null, "MapshotRect exists");
        }
        finally
        {
            mapLoad.QueueFree();
        }
    }

    [Test]
    public void StartLoadingConfiguresLabelsAndShows()
    {
        var scene = GD.Load<PackedScene>("res://scenes/map_load.tscn");
        var mapLoad = scene!.Instantiate<MapLoad>();
        TestScene.AddChild(mapLoad);

        try
        {
            mapLoad.StartLoading("base/church51.ogz", "ctf", "Church 51", "Nieb");

            TestAssert.True(mapLoad.Visible, "MapLoad visible after StartLoading");
            TestAssert.Equal(0.0, mapLoad.ProgressBar!.Value, "Progress starts at 0%");
            TestAssert.Equal("CHURCH 51", mapLoad.TitleLabel!.Text, "Title set");
            TestAssert.True(mapLoad.InfoLabel!.Text.Contains("Nieb"), "Author present in InfoLabel");

            mapLoad.SetProgress(0.40f, "Meshing Geometry & Materials");
            TestAssert.Near(40f, (float)mapLoad.ProgressBar.Value, 0.1f, "Progress updated to 40%");
            TestAssert.True(mapLoad.StatusLabel!.Text.Contains("40%"), "Status label displays 40%");

            mapLoad.Dismiss();
            TestAssert.False(mapLoad.Visible, "MapLoad hidden after Dismiss");
        }
        finally
        {
            mapLoad.QueueFree();
        }
    }

    [Test]
    public void ResolveMapshotFindsThumbnail()
    {
        var texWithPrefix = MapLoad.ResolveMapshot("base/church51");
        TestAssert.True(texWithPrefix != null, "church51 thumbnail resolved with 'base/' prefix");

        var texWithoutPrefix = MapLoad.ResolveMapshot("church51");
        TestAssert.True(texWithoutPrefix != null, "church51 thumbnail resolved without prefix");

        var missing = MapLoad.ResolveMapshot("non_existent_map_xyz");
        TestAssert.True(missing == null, "non-existent map returns null thumbnail gracefully");
    }

    [Test]
    public async Task MapWorldBuildAsyncInvokesProgressSteps()
    {
        var steps = new List<(float Percent, string Status)>();
        Task OnProgress(float percent, string status)
        {
            steps.Add((percent, status));
            return Task.CompletedTask;
        }

        var world = new MapWorld
        {
            OgzPath = "base/church51.ogz",
            ChunkSize = 128,
            ComputeVisuals = false,
            ProgressCallback = OnProgress,
        };

        try
        {
            await world.BuildAsync(OnProgress);

            TestAssert.True(world.Built, "MapWorld built");
            TestAssert.True(steps.Count >= 3, "Progress steps were reported during BuildAsync");
            TestAssert.Near(0.10f, steps[0].Percent, 1e-3f, "First step is Octree parsing at 10%");
            TestAssert.True(steps.Exists(s => Mathf.IsEqualApprox(s.Percent, 0.90f)), "Preloading step reported at 90%");
        }
        finally
        {
            world.QueueFree();
        }
    }
}
