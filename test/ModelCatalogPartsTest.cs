using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Models;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies <see cref="ModelCatalog"/> reads the MD5 animation-part
/// (<c>md5animpart</c>) and pitch (<c>md5pitch</c>) metadata emitted by
/// <c>tools/model-import/import_models.py</c>.
/// </summary>
public class ModelCatalogPartsTest : GoTestBase
{
    public ModelCatalogPartsTest(Node testScene) : base(testScene) { }

    [Test]
    public void ReadsAnimPartsAndPitch()
    {
        const string json = """
        {
          "models": {
            "mrfixit": {
              "glb": "mrfixit.glb",
              "skeleton": true,
              "anims": { "hold 5": "models/mrfixit/sniper_idle.md5anim" },
              "animSpeeds": { "forward": 125.0 },
              "animParts": [ { "mask": ["Spine1"] } ],
              "partAnims": [
                { "hold 5": { "file": "models/mrfixit/sniper_idle.md5anim", "priority": -2 } },
                { "hold 5": { "file": "models/mrfixit/sniper_idle.md5anim", "priority": 0 } }
              ],
              "pitch": [
                { "bone": "Spine1", "scale": 0.75, "offset": 0, "min": -45, "max": 40 },
                { "bone": "Head", "scale": 0.5, "offset": 0, "min": -10, "max": 10 }
              ]
            }
          }
        }
        """;

        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        try
        {
            var catalog = ModelCatalog.Load(path, "/tmp", "res://models")!;
            var info = catalog.Get("mrfixit")!;

            TestAssert.Equal(1, info.AnimParts.Count, "one explicit animation part");
            TestAssert.Equal(1, info.AnimParts[0].Bones.Count, "part 1 masks one bone");
            TestAssert.Equal("Spine1", info.AnimParts[0].Bones[0], "part 1 bone name");

            // partAnims is index-aligned to part 0 + AnimParts.
            TestAssert.Equal(2, info.PartAnims.Count, "two per-part tables");
            TestAssert.Equal(-2, info.PartAnims[0]["hold 5"].Priority, "part 0 hold priority");
            TestAssert.Equal(0, info.PartAnims[1]["hold 5"].Priority, "part 1 hold priority");
            TestAssert.Equal("models/mrfixit/sniper_idle.md5anim",
                info.PartAnims[0]["hold 5"].File!, "part 0 hold file");

            TestAssert.Equal(2, info.Pitches.Count, "two pitch entries");
            TestAssert.Equal("Spine1", info.Pitches[0].Bone, "pitch bone");
            TestAssert.True(Mathf.Abs(info.Pitches[0].Scale - 0.75f) < 1e-6f, "pitch scale");
            TestAssert.True(Mathf.Abs(info.Pitches[0].Min + 45f) < 1e-6f, "pitch min");
            TestAssert.True(Mathf.Abs(info.Pitches[0].Max - 40f) < 1e-6f, "pitch max");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
