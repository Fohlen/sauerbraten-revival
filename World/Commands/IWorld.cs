using Godot;
using SauerbratenRevival.World.Editor;

namespace SauerbratenRevival.World.Commands;

/// <summary>
/// The mutable-world surface a command is applied to. Deliberately small: all the
/// ported Cube2 algorithms live behind it in <see cref="OctaEdit"/>. Applying a
/// command is deterministic — the same command on the same world yields the same
/// result (docs/editor-and-commands.md §1.1, §5.4).
/// </summary>
public interface IWorld
{
    /// <summary>The octree being mutated.</summary>
    Octa Octa { get; }

    /// <summary>Active map entities mutated by entity commands.</summary>
    System.Collections.Generic.List<Entities.MapEntity> Entities { get; }

    /// <summary>Apply a command. Must be deterministic and must not depend on wall-clock state.</summary>
    void Apply(Command cmd);

    /// <summary>Capture a region into a portable block (the pre-state for a destructive edit).</summary>
    OctaEdit.CapturedBlock Capture(Block3 region, int rgrid);
}

/// <summary>
/// The default <see cref="IWorld"/>: routes each <see cref="WorldOp"/> to the
/// verified <see cref="OctaEdit"/> op. This is the single place that turns a
/// serializable command into an octree mutation.
/// </summary>
public sealed class OctaWorld : IWorld
{
    public Octa Octa { get; }
    public System.Collections.Generic.List<Entities.MapEntity> Entities { get; } = new();

    public OctaWorld(Octa octa, System.Collections.Generic.IEnumerable<Entities.MapEntity>? entities = null)
    {
        Octa = octa;
        if (entities != null) Entities.AddRange(entities);
    }

    public OctaEdit.CapturedBlock Capture(Block3 region, int rgrid) => OctaEdit.BlockCopy(Octa, region, rgrid);

    public void Apply(Command cmd)
    {
        switch (cmd.Op)
        {
            case WorldOp.DeleteCube:
                {
                    var p = (DeleteCubePayload)cmd.Payload;
                    OctaEdit.DeleteCube(Octa, p.Selection.ToBlock());
                    break;
                }
            case WorldOp.EditFace:
                {
                    var p = (EditFacePayload)cmd.Payload;
                    var sel = p.Selection.ToSelection();
                    OctaEdit.EditFace(Octa, sel, p.Dir, p.Mode);
                    break;
                }
            case WorldOp.ReplaceBlock:
                {
                    var p = (ReplaceBlockPayload)cmd.Payload;
                    if (p.Block != null)
                        OctaEdit.PasteBlock(Octa, p.Block, p.Target.ToBlock(), p.Target.Grid);
                    break;
                }
            case WorldOp.SetFaceTexture:
                {
                    var p = (SetFaceTexturePayload)cmd.Payload;
                    OctaEdit.ForEachSelCube(Octa, p.Selection.ToBlock(), p.Selection.Grid,
                        (x, y, z, c) => OctaEdit.SetFaceTexture(c, p.Texture, p.Orient));
                    break;
                }
            case WorldOp.ReplaceTexture:
                {
                    var p = (ReplaceTexturePayload)cmd.Payload;
                    OctaEdit.ReplaceTexture(Octa,
                        p.InSelection && p.Selection.HasValue ? p.Selection.Value.ToBlock() : null,
                        p.OldTexture, p.NewTexture, p.InSelection);
                    break;
                }
            case WorldOp.SetFaceMaterial:
                {
                    var p = (SetFaceMaterialPayload)cmd.Payload;
                    OctaEdit.EditMaterial(Octa, p.Selection.ToBlock(), p.MaterialId, p.Filter);
                    break;
                }
            case WorldOp.FlipCube:
                {
                    var p = (TransformCubePayload)cmd.Payload;
                    OctaEdit.FlipSelection(Octa, p.Selection.ToSelection());
                    break;
                }
            case WorldOp.RotateCube:
                {
                    var p = (TransformCubePayload)cmd.Payload;
                    OctaEdit.RotateSelection(Octa, p.Selection.ToSelection(), p.Direction);
                    break;
                }
            case WorldOp.Remip:
                {
                    var p = (RemipPayload)cmd.Payload;
                    OctaEdit.RemipAll(Octa, p.MipVis);
                    break;
                }
            case WorldOp.Snapshot:
                // Lifecycle marker; no world mutation.
                break;
            case WorldOp.NewMap:
                {
                    var p = (NewMapPayload)cmd.Payload;
                    Octa.EmptyMap(p.Scale);
                    break;
                }
            case WorldOp.AddEntity:
                {
                    var p = (EntityPayload)cmd.Payload;
                    Entities.RemoveAll(e => e.Id == p.Id);
                    Entities.Add(p.ToEntity());
                    break;
                }
            case WorldOp.RemoveEntity:
                {
                    var p = (EntityPayload)cmd.Payload;
                    Entities.RemoveAll(e => e.Id == p.Id);
                    break;
                }
            case WorldOp.EditEntity:
                {
                    var p = (EntityPayload)cmd.Payload;
                    int idx = Entities.FindIndex(e => e.Id == p.Id);
                    if (idx >= 0)
                        Entities[idx] = p.ToEntity();
                    else
                        Entities.Add(p.ToEntity());
                    break;
                }
            default:
                throw new System.NotSupportedException($"Unhandled WorldOp {cmd.Op}");
        }
    }
}
