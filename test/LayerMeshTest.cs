using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Texture layers (vslot <c>layer</c>): a face whose vslot has a bottom layer is
/// emitted a second time with that layer's texture (octarender.cpp gencubeverts
/// <c>LAYER_BOTTOM</c> case). Uses <c>bt_falls</c>, which actually uses layers
/// (church51 does not), so the surface counts are the engine's ground truth.
/// </summary>
public class LayerMeshTest : GoTestBase
{
    public LayerMeshTest(Node testScene) : base(testScene) { }

    [Test]
    public void CatalogueResolvesLayerTargets()
    {
        var catalogue = MetadataCatalogue.Load(TestData.CataloguePathFor("bt_falls"));
        TestAssert.Equal(31, catalogue.VSlots[32].Layer, "vslot 32 layer");
        TestAssert.Equal(33, catalogue.VSlots[34].Layer, "vslot 34 layer");
        TestAssert.Equal(0, catalogue.VSlots[31].Layer, "vslot 31 has no layer");
    }

    [Test]
    public void MaterialSetExposesLayerMapping()
    {
        var catalogue = MetadataCatalogue.Load(TestData.CataloguePathFor("bt_falls"));
        var textures = new DumpTextureSource(TestData.ReferenceRoot);
        var set = MaterialBuilder.Build(catalogue, textures);

        TestAssert.Equal(31, set.LayerFor(32), "vslot 32 -> layer 31");
        TestAssert.Equal(33, set.LayerFor(34), "vslot 34 -> layer 33");
        TestAssert.Equal(0, set.LayerFor(31), "vslot 31 -> no layer");
    }

    [Test]
    public void EmitsBottomLayerFaces()
    {
        var catalogue = MetadataCatalogue.Load(TestData.CataloguePathFor("bt_falls"));
        var textures = new DumpTextureSource(TestData.ReferenceRoot);
        var set = MaterialBuilder.Build(catalogue, textures);

        var octa = TestData.ReadMap("bt_falls").ToOcta();
        var withLayers = new OctaMesher(octa, set).BuildChunked(1024);
        var withoutLayers = new OctaMesher(octa, null).BuildChunked(1024);

        // Every layer adds exactly one extra quad (2 triangles) per layered face.
        TestAssert.True(withLayers.TriangleCount > withoutLayers.TriangleCount,
            "layered mesh carries strictly more triangles than the layerless mesh");

        // The layer vslots' materials must now appear as surfaces in the mesh.
        var layerMaterial = set.MaterialFor(31);
        var baseMaterial = set.MaterialFor(32);
        TestAssert.True(layerMaterial != null, "layer vslot 31 has a material");
        TestAssert.True(baseMaterial != null, "base vslot 32 has a material");
        TestAssert.True(layerMaterial != baseMaterial, "layer material differs from base");

        int layerSurfaces = 0, baseSurfaces = 0;
        foreach (var chunk in withLayers.Chunks)
            for (int s = 0; s < chunk.Mesh.GetSurfaceCount(); s++)
            {
                var m = chunk.Mesh.SurfaceGetMaterial(s);
                if (m == layerMaterial) layerSurfaces++;
                if (m == baseMaterial) baseSurfaces++;
            }

        TestAssert.True(baseSurfaces > 0, "base vslot surfaces present");
        TestAssert.True(layerSurfaces > 0, "layer vslot surfaces emitted");

        // With no material set there is no layer resolution, so the geometry
        // equals the single-layer pass.
        var noMaterials = new OctaMesher(octa, null).BuildChunked(1024);
        TestAssert.Equal(withoutLayers.TriangleCount, noMaterials.TriangleCount,
            "no material set -> no layer faces");
    }
}
