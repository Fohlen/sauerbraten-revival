using System;
using System.IO;
using Godot;
using Chickensoft.GoDotTest;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies the OGZ lightmap-atlas reader against an **independent oracle**:
/// the reference engine's own <c>dumplms</c> command, which writes each atlas to
/// <c>lightmap_&lt;map&gt;_&lt;i&gt;.png</c> via the engine's PNG writer — code that
/// shares nothing with our reader. Because <c>load_world</c> only calls
/// <c>LightMap::finalize()</c> (which clears the pack tree, not the pixels), the
/// dumped PNG pixels are exactly the raw atlas bytes stored in the map file.
///
/// So the reward signal is: <c>reader.Lightmaps[i].Pixels == decode(golden PNG)</c>,
/// byte for byte. The goldens are committed under <c>test/golden/lightmaps/</c>
/// and regenerated with:
///
///   printf 'dumplms\nquit\n' &gt; /tmp/dump.cfg
///   sauer_client -lbase/frag-lab -x"exec /tmp/dump.cfg"
///   cp lightmap_frag-lab_*.png test/golden/lightmaps/
///
/// (from the reference dump root; see tools/ogztool/README.md).
///
/// <c>frag-lab</c> is chosen because it has 13 atlases covering both paths:
/// 12 RGB (bpp 3) and one RGBA (<c>lightmap_frag-lab_3</c>, <c>LM_ALPHA</c>,
/// bpp 4 — the layer/blend-mask atlas).
/// </summary>
public class OgzLightmapTest : GoTestBase
{
    private const string Map = "frag-lab";

    /// <summary>LM_PACKW × LM_PACKH (engine lightmap.h).</summary>
    private const int PackW = 512, PackH = 512;

    private static string GoldenDir =>
        Path.Combine(ProjectSettings.GlobalizePath("res://"), "test", "golden", "lightmaps");

    public OgzLightmapTest(Node testScene) : base(testScene) { }

    [Test]
    public void AtlasCountMatchesHeader()
    {
        var map = TestData.ReadMap(Map);
        TestAssert.Equal(map.Header.Lightmaps, map.Lightmaps.Count, "atlas count == hdr.lightmaps");
    }

    [Test]
    public void AtlasesMatchDumpLmsGolden()
    {
        var map = TestData.ReadMap(Map);
        TestAssert.True(map.Lightmaps.Count > 0, "map has atlases");

        for (int i = 0; i < map.Lightmaps.Count; i++)
        {
            var atlas = map.Lightmaps[i];
            string goldenPath = Path.Combine(GoldenDir, $"lightmap_{Map}_{i}.png");
            TestAssert.True(File.Exists(goldenPath), $"golden exists: {goldenPath}");

            // The engine writes the atlas with savepng(..., flip=true), so the PNG
            // rows are vertically mirrored relative to the stored buffer. Decode,
            // unflip, then compare to the raw pixels the reader produced.
            var image = new Image();
            var err = image.LoadPngFromBuffer(File.ReadAllBytes(goldenPath));
            TestAssert.Equal((int)Error.Ok, (int)err, $"decode {goldenPath}");
            image.FlipY();

            int channels = image.GetFormat() switch
            {
                Image.Format.Rgb8 => 3,
                Image.Format.Rgba8 => 4,
                Image.Format.L8 => 1,
                var f => throw new InvalidOperationException($"unexpected golden format {f}"),
            };

            TestAssert.Equal(PackW, image.GetWidth(), $"atlas {i} width");
            TestAssert.Equal(PackH, image.GetHeight(), $"atlas {i} height");
            TestAssert.Equal(channels, atlas.Bpp, $"atlas {i} bpp matches golden channels");

            byte[] expected = image.GetData();
            TestAssert.Equal(expected.Length, atlas.Pixels.Length, $"atlas {i} byte length");

            // Report the first diff (if any) rather than just the boolean.
            for (int b = 0; b < expected.Length; b++)
            {
                if (expected[b] != atlas.Pixels[b])
                {
                    TestAssert.True(false,
                        $"atlas {i} byte {b}: reader={atlas.Pixels[b]} golden={expected[b]}");
                    return;
                }
            }
        }
    }

    [Test]
    public void AtlasTypeByteDecodesBppAndAlpha()
    {
        // The reader must derive bpp from the type byte exactly as load_world does:
        //   bpp = 4 when (type & LM_ALPHA) && (type & LM_TYPE) != LM_BUMPMAP1, else 3.
        var map = TestData.ReadMap(Map);
        foreach (var atlas in map.Lightmaps)
        {
            int lmType = atlas.Type & 0x0F;
            bool alpha = (atlas.Type & 0x10) != 0;   // LM_ALPHA
            int expectedBpp = alpha && lmType != 2 ? 4 : 3;
            TestAssert.Equal(expectedBpp, atlas.Bpp, $"atlas type 0x{atlas.Type:x2} bpp");
            TestAssert.Equal(expectedBpp * PackW * PackH, atlas.Pixels.Length,
                $"atlas type 0x{atlas.Type:x2} payload size");
        }
    }
}
