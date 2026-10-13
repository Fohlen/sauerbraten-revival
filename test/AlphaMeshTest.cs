using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Transparent (<c>MAT_ALPHA</c>) blends. The fixtures we have catalogues for
/// (church51/bt_falls) use no alpha, so this builds a synthetic world and checks:
///   * an alpha cube's face is emitted even against a solid neighbour
///     (octa.cpp visibletris treats MAT_ALPHA as see-through), and
///   * it lands in its own surface keyed by <see cref="MaterialSet.AlphaBit"/>,
///     with a transparent material distinct from the opaque variant.
/// </summary>
public class AlphaMeshTest : GoTestBase
{
    public AlphaMeshTest(Node testScene) : base(testScene) { }

    private static Octa AlphaWorld(ushort material)
    {
        var octa = new Octa(10);
        octa.EmptyMap(10); // low-Z octants solid (floor)
        // Mark the first floor leaf as an alpha-material cube.
        var leaf = octa.WorldRoot[0].Children is { } kids ? FindSolidLeaf(kids) : octa.WorldRoot[0];
        leaf.Material = material;
        return octa;
    }

    private static Cube FindSolidLeaf(Cube[] cubes)
    {
        foreach (var c in cubes)
        {
            if (c.Children != null) return FindSolidLeaf(c.Children);
            if (c.IsEntirelySolid()) return c;
        }
        return cubes[0];
    }

    [Test]
    public void AlphaMaterialKeyIsDistinct()
    {
        var set = new MaterialSet();
        var opaque = new StandardMaterial3D();
        var transparent = new ShaderMaterial();
        var uv = new[] { new TexGen(new Vector4(1, 0, 0, 0), new Vector4(0, 1, 0, 0)) };
        set.Add(7, opaque, uv);
        set.SetAlphaMaterial(7, transparent);

        TestAssert.True(set.HasAlpha(7), "vslot 7 has an alpha variant");
        TestAssert.True(ReferenceEquals(set.MaterialFor(7), opaque), "plain key -> opaque material");
        TestAssert.True(ReferenceEquals(set.MaterialFor(set.Key(7, true)), transparent), "alpha key -> transparent material");
        TestAssert.Equal(7, set.Key(7, false), "opaque key is the vslot");
        TestAssert.Equal(7 | MaterialSet.AlphaBit, set.Key(7, true), "alpha key sets the alpha bit");
    }

    [Test]
    public void AlphaMaterialShaderBuilds()
    {
        var material = AlphaMaterial.Create(null, null, null, Colors.White, Colors.White, 0.5f, 0.0f);
        TestAssert.True(material.Shader != null, "alpha material has a shader");
        TestAssert.True(material.Shader!.Code.Contains("FRONT_FACING"), "shader selects per-side alpha");
        TestAssert.True(((float)material.GetShaderParameter("alphafront")) == 0.5f, "alphafront flows through");
    }

    [Test]
    public void AlphaMaterialCutoutShaderBuilds()
    {
        var cutout = AlphaMaterial.CreateCutout(null, null, null, Colors.White, Colors.White, 0.7f);
        TestAssert.True(cutout.Shader != null, "cutout material has a shader");
        TestAssert.True(cutout.Shader!.Code.Contains("alpha_scissor"), "shader contains alpha_scissor uniform");
        TestAssert.True(cutout.Shader.Code.Contains("discard"), "shader contains discard instruction");
        TestAssert.True(Mathf.IsEqualApprox(0.7f, (float)cutout.GetShaderParameter("alpha_scissor")), "alpha_scissor parameter set");
    }

    [Test]
    public void MaterialBuilderSetsAlphaTransparencyOnAlphaWorldShaders()
    {
        var catalogue = new MetadataCatalogue();
        catalogue.Slots[1] = new CatalogueSlot { Index = 1, Shader = "alphaworld", DiffusePath = "test.png" };
        catalogue.VSlots[1] = new CatalogueVSlot { Index = 1, Slot = 1 };

        catalogue.Slots[2] = new CatalogueSlot { Index = 2, Shader = "stdworld", DiffusePath = "test.png" };
        catalogue.VSlots[2] = new CatalogueVSlot { Index = 2, Slot = 2 };

        var textures = new DummyTextureSource();
        var set = MaterialBuilder.Build(catalogue, textures);

        var alphaMat = set.MaterialFor(1) as StandardMaterial3D;
        TestAssert.True(alphaMat != null, "alpha slot material created");
        TestAssert.Equal(BaseMaterial3D.TransparencyEnum.Alpha, alphaMat!.Transparency, "alphaworld slot uses Transparency.Alpha");
        TestAssert.Equal(BaseMaterial3D.DepthDrawModeEnum.Always, alphaMat.DepthDrawMode, "alphaworld slot uses DepthDrawMode.Always");

        var stdMat = set.MaterialFor(2) as StandardMaterial3D;
        TestAssert.True(stdMat != null, "std slot material created");
        TestAssert.Equal(BaseMaterial3D.TransparencyEnum.Disabled, stdMat!.Transparency, "stdworld slot uses Transparency.Disabled");
    }

    private sealed class DummyTextureSource : ITextureSource
    {
        public Texture2D? Get(string path) => null;
        public Vector2I Size(string path) => new(64, 64);
        public Image? GetImage(string path) => null;
    }

    [Test]
    public void AlphaCubeFacesAreEmittedAgainstSolidNeighbour()
    {
        // With MAT_ALPHA the emitted surface must be the transparent variant while
        // non-alpha geometry stays opaque.
        var octa = AlphaWorld(Octa.MAT_ALPHA);
        var catalogue = MetadataCatalogue.Load(TestData.CataloguePath);
        var textures = new DumpTextureSource(TestData.ReferenceRoot);
        var set = MaterialBuilder.Build(catalogue, textures);

        var mesh = new OctaMesher(octa, set).BuildChunked(256);

        int transparentSurfaces = 0, opaqueSurfaces = 0;
        foreach (var chunk in mesh.Chunks)
            for (int s = 0; s < chunk.Mesh.GetSurfaceCount(); s++)
            {
                if (chunk.Mesh.SurfaceGetMaterial(s) is ShaderMaterial) transparentSurfaces++;
                else opaqueSurfaces++;
            }

        TestAssert.True(transparentSurfaces > 0, "alpha cube emits a transparent surface");
        TestAssert.True(opaqueSurfaces > 0, "non-alpha geometry still emits opaque surfaces");

        // Alpha only changes material grouping, never geometry.
        var noMat = new OctaMesher(octa, null).BuildChunked(256);
        TestAssert.Equal(mesh.TriangleCount, noMat.TriangleCount, "alpha changes materials, not geometry");
    }

    [Test]
    public void AlphaFaceIsNotCulledAgainstSolid()
    {
        // Two adjacent leaves: a solid one and an alpha one.
        var octa = new Octa(10);
        octa.EmptyMap(10);

        var parent = octa.WorldRoot[0];
        OctaEdit.SubdivideCube(octa, parent);
        var a = parent.Children![0];
        var b = parent.Children![1];
        a.SetFaces(Octa.F_SOLID);
        b.SetFaces(Octa.F_SOLID);
        a.Material = Octa.MAT_AIR;
        b.Material = Octa.MAT_ALPHA;

        // The alpha cube's faces survive the solid neighbour's cull.
        var coB = new Vector3I(1 << 9, 0, 0);
        bool anyVisible = false;
        for (int o = 0; o < 6; o++)
            if (OctaFaceWalker.FaceVisible(octa, b, o, coB, 1 << 9, false)) anyVisible = true;
        TestAssert.True(anyVisible, "alpha cube faces are visible against a solid neighbour");

        // And the solid cube's face against the alpha neighbour stays visible too,
        // so the solid is seen through the alpha (octa.cpp visibletris).
        var coA = Vector3I.Zero;
        bool solidVisible = false;
        for (int o = 0; o < 6; o++)
            if (OctaFaceWalker.FaceVisible(octa, a, o, coA, 1 << 9, false)) solidVisible = true;
        TestAssert.True(solidVisible, "solid face against an alpha neighbour stays visible");
    }
}
