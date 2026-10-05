using System;
using System.IO;
using System.Text.Json;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Parses a real map from the metadata catalogue and asserts the reader against
/// the engine's exported ground truth. The catalogue is produced by
/// tools/refharness/patches/ogz-metadata.patch.
/// </summary>
public class OgzReaderTest : TestClass
{
    private static JsonElement Catalogue()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestData.CataloguePath));
        return doc.RootElement.Clone();
    }

    private static OgzMap ReadMap() => TestData.ReadMap();

    public OgzReaderTest(Node testScene) : base(testScene) { }

    [Test]
    public void HeaderMatchesCatalogue()
    {
        var map = ReadMap();
        var cat = Catalogue();

        TestAssert.Equal("OCTA", map.Header.Magic, "magic");
        TestAssert.Equal(33, map.Header.Version, "mapversion");
        TestAssert.Equal(40, map.Header.HeaderSize, "headersize");
        TestAssert.Equal(cat.GetProperty("worldsize").GetInt32(), map.Header.WorldSize, "worldsize");
        TestAssert.Equal(cat.GetProperty("entities").GetArrayLength(), map.Header.NumEnts, "numents");
        TestAssert.Equal(map.Header.NumVSlots, map.VSlots.Count, "numvslots (loaded vslots)");
    }

    [Test]
    public void EntityPositionsMatchCatalogue()
    {
        var map = ReadMap();
        var cat = Catalogue();
        var ents = cat.GetProperty("entities");
        TestAssert.Equal(ents.GetArrayLength(), map.Entities.Count, "entity count");

        for (int i = 0; i < ents.GetArrayLength(); i++)
        {
            var json = ents[i];
            var ent = map.Entities[i];
            var pos = json.GetProperty("position");
            TestAssert.True(Mathf.Abs((float)pos[0].GetDouble() - ent.Position.X) < 0.01f, $"entity {i} x");
            TestAssert.True(Mathf.Abs((float)pos[1].GetDouble() - ent.Position.Y) < 0.01f, $"entity {i} y");
            TestAssert.True(Mathf.Abs((float)pos[2].GetDouble() - ent.Position.Z) < 0.01f, $"entity {i} z");
            TestAssert.Equal(json.GetProperty("attr1").GetInt32(), (int)ent.Attr1, $"entity {i} attr1");
            TestAssert.Equal(json.GetProperty("attr2").GetInt32(), (int)ent.Attr2, $"entity {i} attr2");
            TestAssert.Equal(json.GetProperty("attr3").GetInt32(), (int)ent.Attr3, $"entity {i} attr3");
            TestAssert.Equal(json.GetProperty("attr4").GetInt32(), (int)ent.Attr4, $"entity {i} attr4");
            TestAssert.Equal(json.GetProperty("attr5").GetInt32(), (int)ent.Attr5, $"entity {i} attr5");
        }
    }

    // texture.h VSLOT_* bit indices.
    private const int VSlotShParam = 0, VSlotScale = 1, VSlotRotation = 2, VSlotOffset = 3,
                      VSlotScroll = 4, VSlotLayer = 5, VSlotAlpha = 6, VSlotColor = 7;

    [Test]
    public void VSlotsMatchCatalogue()
    {
        var map = ReadMap();
        var cat = Catalogue();
        int compared = 0, changedFields = 0;

        foreach (var json in cat.GetProperty("vslots").EnumerateArray())
        {
            int index = json.GetProperty("index").GetInt32();
            TestAssert.True(index >= 0 && index < map.VSlots.Count, $"vslot {index} in range");
            var vs = map.VSlots[index];
            if (vs.Changed == 0) continue;
            compared++;

            // The engine overlay-inherits unchanged fields from the base vslot (which
            // the map cfg's texture commands populate), so only the fields the map
            // actually stored (its `changed` bits) are authoritative in the file.
            if ((vs.Changed & (1 << VSlotScale)) != 0)
            {
                TestAssert.True(Mathf.Abs((float)json.GetProperty("scale").GetDouble() - vs.Scale) < 1e-4f, $"vslot {index} scale");
                changedFields++;
            }
            if ((vs.Changed & (1 << VSlotRotation)) != 0)
            {
                TestAssert.Equal(json.GetProperty("rotation").GetInt32(), vs.Rotation, $"vslot {index} rotation");
                changedFields++;
            }
            if ((vs.Changed & (1 << VSlotOffset)) != 0)
            {
                TestAssert.Equal(json.GetProperty("offset")[0].GetInt32(), vs.OffsetX, $"vslot {index} offset.x");
                TestAssert.Equal(json.GetProperty("offset")[1].GetInt32(), vs.OffsetY, $"vslot {index} offset.y");
                changedFields++;
            }
            if ((vs.Changed & (1 << VSlotScroll)) != 0)
            {
                TestAssert.True(Mathf.Abs((float)json.GetProperty("scroll")[0].GetDouble() - vs.ScrollX) < 1e-4f, $"vslot {index} scroll.x");
                TestAssert.True(Mathf.Abs((float)json.GetProperty("scroll")[1].GetDouble() - vs.ScrollY) < 1e-4f, $"vslot {index} scroll.y");
                changedFields++;
            }
            if ((vs.Changed & (1 << VSlotLayer)) != 0)
            {
                TestAssert.Equal(json.GetProperty("layer").GetInt32(), vs.Layer, $"vslot {index} layer");
                changedFields++;
            }
            if ((vs.Changed & (1 << VSlotAlpha)) != 0)
            {
                TestAssert.True(Mathf.Abs((float)json.GetProperty("alpha")[0].GetDouble() - vs.AlphaFront) < 1e-4f, $"vslot {index} alphafront");
                TestAssert.True(Mathf.Abs((float)json.GetProperty("alpha")[1].GetDouble() - vs.AlphaBack) < 1e-4f, $"vslot {index} alphaback");
                changedFields++;
            }
            if ((vs.Changed & (1 << VSlotColor)) != 0)
            {
                var color = json.GetProperty("color");
                TestAssert.True(Mathf.Abs((float)color[0].GetDouble() - vs.ColorScale.X) < 1e-4f, $"vslot {index} color.r");
                TestAssert.True(Mathf.Abs((float)color[1].GetDouble() - vs.ColorScale.Y) < 1e-4f, $"vslot {index} color.g");
                TestAssert.True(Mathf.Abs((float)color[2].GetDouble() - vs.ColorScale.Z) < 1e-4f, $"vslot {index} color.b");
                changedFields++;
            }
            if ((vs.Changed & (1 << VSlotShParam)) != 0)
            {
                // Stored params come first; the engine appends inherited base params.
                var jsonParams = json.GetProperty("params");
                TestAssert.True(vs.Params.Count <= jsonParams.GetArrayLength(), $"vslot {index} param count");
                for (int p = 0; p < vs.Params.Count; p++)
                    TestAssert.Equal(jsonParams[p].GetProperty("name").GetString(), vs.Params[p].Name, $"vslot {index} param {p} name");
                changedFields++;
            }
        }

        GD.Print($"[ogz] checked {compared} stored vslots / {changedFields} changed fields against the catalogue");
        TestAssert.True(compared > 0 && changedFields > 0, "at least one changed vslot field was checked");
    }

    [Test]
    public void GeometryMatchesCatalogue()
    {
        var map = ReadMap();
        var stats = OgzGeometry.Compute(map.WorldRoot);
        GD.Print($"[ogz] geometry: nodes={stats.Nodes} leaves={stats.Leaves} " +
                 $"empty={stats.Empty} solid={stats.Solid} normal={stats.Normal} " +
                 $"surfaces={stats.Surfaces} verts={stats.Verts} " +
                 $"checksum={stats.Checksum:x16} textures={stats.TextureFaces.Count}");

        var cat = Catalogue();
        if (!cat.TryGetProperty("geometry", out var geo))
        {
            GD.Print("[ogz] catalogue has no geometry block yet; run the patched client to regenerate it");
            return;
        }

        TestAssert.Equal(geo.GetProperty("nodes").GetInt32(), stats.Nodes, "nodes");
        TestAssert.Equal(geo.GetProperty("leaves").GetInt32(), stats.Leaves, "leaves");
        TestAssert.Equal(geo.GetProperty("empty").GetInt32(), stats.Empty, "empty");
        TestAssert.Equal(geo.GetProperty("solid").GetInt32(), stats.Solid, "solid");
        TestAssert.Equal(geo.GetProperty("normal").GetInt32(), stats.Normal, "normal");
        TestAssert.Equal(geo.GetProperty("surfaces").GetInt32(), stats.Surfaces, "surfaces");
        TestAssert.Equal(geo.GetProperty("verts").GetInt32(), stats.Verts, "verts");
        TestAssert.Equal(geo.GetProperty("checksum").GetString(), $"{stats.Checksum:x16}", "checksum");

        var hist = geo.GetProperty("textureFaces");
        foreach (var entry in hist.EnumerateArray())
        {
            int texture = entry.GetProperty("texture").GetInt32();
            int count = entry.GetProperty("count").GetInt32();
            stats.TextureFaces.TryGetValue(texture, out int actual);
            TestAssert.Equal(count, actual, $"textureFaces[{texture}]");
        }
        TestAssert.Equal(hist.GetArrayLength(), stats.TextureFaces.Count, "textureFaces distinct count");
    }
}
