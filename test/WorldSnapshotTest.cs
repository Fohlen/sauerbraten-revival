using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Runtime.Formats.Ogz;
using SauerbratenRevival.World.Runtime.Formats.Save;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers M5's core: the canonical <see cref="WorldSnapshot"/> encode/decode and
/// content hash (docs/editor-and-commands.md §4, §5.1, §7). A snapshot must round-trip
/// an octree byte-for-byte (geometry checksum equal) and hash deterministically.
/// </summary>
public class WorldSnapshotTest : GoTestBase
{
    public WorldSnapshotTest(Node testScene) : base(testScene) { }

    private static ulong Checksum(Octa octa) => OgzGeometry.Compute(octa.WorldRoot).Checksum;

    private static Octa SmallEditedWorld()
    {
        var octa = new Octa(8);
        octa.EmptyMap(8);
        var sink = new CommandSink(new OctaWorld(octa));
        // A handful of edits so the octree has normal (deformed-capable) leaves.
        sink.Submit(new Command(WorldOp.EditFace,
            new EditFacePayload(new SelectionPayload(new Vector3I(64, 64, 64), new Vector3I(2, 2, 2), 8, 0), -1, 1)));
        sink.Submit(new Command(WorldOp.DeleteCube,
            new DeleteCubePayload(new SelectionPayload(new Vector3I(80, 80, 80), new Vector3I(1, 1, 1), 8, 0))));
        sink.Submit(new Command(WorldOp.SetFaceTexture,
            new SetFaceTexturePayload(new SelectionPayload(new Vector3I(72, 72, 72), new Vector3I(1, 1, 1), 8, 0), 5, -1)));
        return octa;
    }

    [Test]
    public void EmptyWorldRoundTrips()
    {
        var octa = new Octa(6);
        var snap = WorldSnapshot.Capture(octa);
        var back = WorldSnapshot.Decode(snap.Encode());
        TestAssert.Equal(octa.WorldScale, back.Scale, "scale");
        TestAssert.Equal(Checksum(octa), OgzGeometry.Compute(back.Root).Checksum, "geometry checksum");
    }

    [Test]
    public void EditedWorldRoundTrips()
    {
        var octa = SmallEditedWorld();
        var snap = WorldSnapshot.Capture(octa);
        var back = WorldSnapshot.Decode(snap.Encode()).ToOcta();
        TestAssert.Equal(octa.WorldSize, back.WorldSize, "worldsize");
        TestAssert.Equal(Checksum(octa), Checksum(back), "geometry checksum after edits");
    }

    [Test]
    public void EncodeIsDeterministic()
    {
        var octa = SmallEditedWorld();
        var a = WorldSnapshot.Capture(octa).Encode();
        var b = WorldSnapshot.Capture(octa).Encode();
        TestAssert.True(System.Linq.Enumerable.SequenceEqual(a, b), "two captures encode identically");
    }

    [Test]
    public void HashIsStableAcrossRoundTrip()
    {
        var octa = SmallEditedWorld();
        ulong before = WorldSnapshot.Capture(octa).Hash();
        ulong after = WorldSnapshot.Decode(WorldSnapshot.Capture(octa).Encode()).Hash();
        TestAssert.Equal(before, after, "content hash survives encode/decode");
    }

    [Test]
    public void HashDistinguishesEdits()
    {
        var octa = new Octa(6);
        ulong before = WorldSnapshot.Capture(octa).Hash();

        var sink = new CommandSink(new OctaWorld(octa));
        sink.Submit(new Command(WorldOp.SetFaceTexture,
            new SetFaceTexturePayload(new SelectionPayload(new Vector3I(0, 0, 0), new Vector3I(1, 1, 1), 8, 0), 9, -1)));

        TestAssert.True(WorldSnapshot.Capture(octa).Hash() != before, "hash changes after a texture edit");
    }

    [Test]
    public void RejectsUnknownVersion()
    {
        var octa = new Octa(6);
        byte[] data = WorldSnapshot.Capture(octa).Encode();
        data[4] = 0xEE; // corrupt the format version (after the 4-byte magic)
        var threw = false;
        try { WorldSnapshot.Decode(data); }
        catch (System.IO.InvalidDataException) { threw = true; }
        TestAssert.True(threw, "unknown version is rejected");
    }

    [Test]
    public void EntitiesRoundTrip()
    {
        var snap = new WorldSnapshot { Scale = 10, Root = new Octa(10).WorldRoot };
        snap.Entities.Add(new MapEntity { Type = 3, Position = new Vector3(1, 2, -3), Attr1 = 1, Attr2 = 2, Attr3 = 3, Attr4 = 4, Attr5 = 5 });

        var back = WorldSnapshot.Decode(snap.Encode());
        TestAssert.Equal(1, back.Entities.Count, "entity count");
        TestAssert.Equal((byte)3, back.Entities[0].Type, "type");
        TestAssert.Equal(16, back.Entities[0].FixedPosition.X, "x fixed-point");
        TestAssert.Equal(-48, back.Entities[0].FixedPosition.Z, "z fixed-point");
        TestAssert.Equal((short)5, back.Entities[0].Attr5, "attr5");
    }

    [Test]
    public void EntityHashSurvivesQuantization()
    {
        // Entities keep float positions in memory (that is what `.ogz` stores), but the
        // snapshot quantizes to FixedPoint.DMF when it is written. So hashing a live
        // world and its reloaded snapshot must agree even for a sub-1/16 position —
        // otherwise multiplayer parity breaks the moment a map is saved and reloaded.
        var snap = new WorldSnapshot { Scale = 10, Root = new Octa(10).WorldRoot };
        snap.Entities.Add(new MapEntity { Type = 3, Position = new Vector3(0.3f, 1.7f, -2.111f) });

        var back = WorldSnapshot.Decode(snap.Encode());
        TestAssert.Equal(5, back.Entities[0].FixedPosition.X, "0.3 rounds to 5/16");
        TestAssert.Equal(27, back.Entities[0].FixedPosition.Y, "1.7 rounds to 27/16");
        TestAssert.Equal(-34, back.Entities[0].FixedPosition.Z, "-2.111 rounds to -34/16");
        TestAssert.Equal(snap.Hash(), back.Hash(), "hash parity across save/load");
    }

    [Test]
    public void Church51LightmapLinkSurvivesRoundTrip()
    {
        // The baked-lightmap link (CubeExt) is NOT derivable from faces/edges, so it
        // must be persisted (v2). Assert it survives directly — presence, per-face
        // lmid/numverts, and every vertex's u/v/norm — rather than only via the
        // aggregate geometry checksum, so a regression is pinpointed.
        var octa = TestData.ReadMap().ToOcta();
        var back = WorldSnapshot.Decode(WorldSnapshot.Capture(octa).Encode()).ToOcta();

        int extCubes = 0, faces = 0, verts = 0, diff = -1;
        void Walk(Cube[] a, Cube[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].Children != null)
                {
                    Walk(a[i].Children!, b[i].Children!);
                    continue;
                }
                TestAssert.Equal(a[i].Ext != null, b[i].Ext != null, $"ext presence at cube {i}");
                if (a[i].Ext == null) continue;
                extCubes++;
                for (int f = 0; f < 6; f++)
                {
                    var sa = a[i].Ext!.Surfaces[f];
                    var sb = b[i].Ext!.Surfaces[f];
                    TestAssert.Equal(sa.Lmid0, sb.Lmid0, $"face {f} lmid0");
                    TestAssert.Equal(sa.Lmid1, sb.Lmid1, $"face {f} lmid1");
                    TestAssert.Equal(sa.NumVerts, sb.NumVerts, $"face {f} numverts");
                    TestAssert.Equal(sa.Verts, sb.Verts, $"face {f} verts offset");
                    int total = sa.TotalVerts();
                    if (total > 0) faces++;
                    for (int k = 0; k < total; k++)
                    {
                        var va = a[i].Ext!.Verts[sa.Verts + k];
                        var vb = b[i].Ext!.Verts[sb.Verts + k];
                        if (diff < 0 && (va.X != vb.X || va.Y != vb.Y || va.Z != vb.Z ||
                                         va.U != vb.U || va.V != vb.V || va.Norm != vb.Norm))
                            diff = k;
                        verts++;
                    }
                }
            }
        }
        Walk(octa.WorldRoot, back.WorldRoot);

        TestAssert.True(extCubes > 0, "church51 has cubes with surface data");
        TestAssert.True(faces > 0, "church51 has faces with lightmap vertices");
        TestAssert.True(verts > 0, "church51 has lightmap vertices");
        TestAssert.Equal(-1, diff, "no vertex differs (x/y/z/u/v/norm)");
    }

    // ---- real map fidelity (like the OGZ golden tests) ----

    [Test]
    public void Church51RoundTripsExactly()
    {
        var map = TestData.ReadMap();
        var octa = map.ToOcta();
        ulong before = Checksum(octa);

        var back = WorldSnapshot.Decode(WorldSnapshot.Capture(octa).Encode()).ToOcta();

        TestAssert.Equal(octa.WorldScale, back.WorldScale, "worldscale");
        TestAssert.Equal(before, Checksum(back), "church51 geometry survives the native format");
    }

    [Test]
    public void Church51HashIsStable()
    {
        var octa = TestData.ReadMap().ToOcta();
        ulong first = WorldSnapshot.Capture(octa).Hash();
        ulong second = WorldSnapshot.Decode(WorldSnapshot.Capture(octa).Encode()).Hash();
        TestAssert.Equal(first, second, "church51 content hash is round-trip stable");
    }
}
