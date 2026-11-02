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

            mapLoad.AddProgress(0.20f, "Parsing Octree & Entities");
            TestAssert.Near(20f, (float)mapLoad.ProgressBar.Value, 0.1f, "Progress incremented to 20%");
            TestAssert.True(mapLoad.StatusLabel!.Text.Contains("20%"), "Status label displays 20%");

            mapLoad.StepProgress("Radiance Hints & Lighting");
            TestAssert.Near(40f, (float)mapLoad.ProgressBar.Value, 0.1f, "Progress incremented to 40%");

            mapLoad.Dismiss();
            TestAssert.False(mapLoad.Visible, "MapLoad hidden after Dismiss");
        }
        finally
        {
            mapLoad.QueueFree();
        }
    }

    [Test]
    public void FiveStepLoadingReachesHundredPercent()
    {
        var scene = GD.Load<PackedScene>("res://scenes/map_load.tscn");
        var mapLoad = scene!.Instantiate<MapLoad>();
        TestScene.AddChild(mapLoad);

        try
        {
            mapLoad.StartLoading("base/church51.ogz", "ctf");
            TestAssert.Near(0f, (float)mapLoad.ProgressBar!.Value, 0.1f, "Starts at 0%");

            mapLoad.AddProgress(0.20f, "Step 1");
            TestAssert.Near(20f, (float)mapLoad.ProgressBar.Value, 0.1f, "Step 1 at 20%");

            mapLoad.AddProgress(0.20f, "Step 2");
            TestAssert.Near(40f, (float)mapLoad.ProgressBar.Value, 0.1f, "Step 2 at 40%");

            mapLoad.AddProgress(0.20f, "Step 3");
            TestAssert.Near(60f, (float)mapLoad.ProgressBar.Value, 0.1f, "Step 3 at 60%");

            mapLoad.AddProgress(0.20f, "Step 4");
            TestAssert.Near(80f, (float)mapLoad.ProgressBar.Value, 0.1f, "Step 4 at 80%");

            mapLoad.AddProgress(0.20f, "Step 5");
            TestAssert.Near(100f, (float)mapLoad.ProgressBar.Value, 0.1f, "Step 5 reaches 100%");
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
        var steps = new List<(float Increment, string Status)>();
        Task OnProgress(float increment, string status)
        {
            steps.Add((increment, status));
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
            TestAssert.Equal(4, steps.Count, "4 progress steps reported during BuildAsync");
            foreach (var step in steps)
            {
                TestAssert.Near(0.20f, step.Increment, 1e-3f, "Each step passes +20% (0.20f) increment");
            }
        }
        finally
        {
            world.QueueFree();
        }
    }
}
