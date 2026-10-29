using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Editor;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Network;

namespace SauerbratenRevival.World.Commands;

/// <summary>
/// The mutable-world surface a command is applied to.
/// Deliberately small: all the ported Cube2 algorithms live behind it in <see cref="OctaEdit"/>.
/// Applying a command is deterministic — the same command on the same world yields the same
/// result (docs/editor-and-commands.md §1.1, §5.4).
/// </summary>
public sealed class OctaWorld
{
    public Octa Octa { get; }
    public List<MapEntity> Entities { get; } = new();
    public SetEnvironmentCmd? Environment { get; set; }
    public KeyframePackage? LatestKeyframe { get; private set; }

    public OctaWorld(Octa octa, IEnumerable<MapEntity>? entities = null, SetEnvironmentCmd? environment = null)
    {
        Octa = octa;
        if (entities != null) Entities.AddRange(entities);
        Environment = environment;
    }

    public OctaEdit.CapturedBlock Capture(Block3 region, int rgrid) => OctaEdit.BlockCopy(Octa, region, rgrid);

    public void Apply(Command cmd)
    {
        if (!cmd.Payload.HasValue) return;
        var payload = cmd.Payload.Value;

        switch (cmd.Op)
        {
            case WorldOp.DeleteCube:
                {
                    var p = payload.Item4;
                    OctaEdit.DeleteCube(Octa, p.Sel.ToBlock());
                    break;
                }
            case WorldOp.EditFace:
                {
                    var p = payload.Item1;
                    OctaEdit.EditFace(Octa, p.Sel.ToSelection(), p.Dir, p.Mode);
                    break;
                }
            case WorldOp.ReplaceBlock:
                {
                    var p = payload.Item12;
                    if (p.Data.HasValue)
                    {
                        var block = NetMessage.DeserializeBlock(p.Data.Value.ToArray());
                        OctaEdit.PasteBlock(Octa, block, p.Sel.ToBlock(), p.Sel.Grid);
                    }
                    break;
                }
            case WorldOp.SetFaceTexture:
                {
                    var p = payload.Item2;
                    OctaEdit.ForEachSelCube(Octa, p.Sel.ToBlock(), p.Sel.Grid,
                        (x, y, z, c) => OctaEdit.SetFaceTexture(c, p.TextureId, p.Flags));
                    break;
                }
            case WorldOp.ReplaceTexture:
                {
                    var p = payload.Item3;
                    OctaEdit.ReplaceTexture(Octa,
                        p.InSelection && p.Sel != null ? p.Sel.ToBlock() : null,
                        p.OldTexture, p.NewTexture, p.InSelection);
                    break;
                }
            case WorldOp.SetFaceMaterial:
                {
                    var p = payload.Item10;
                    OctaEdit.EditMaterial(Octa, p.Sel.ToBlock(), p.MaterialId, p.Filter);
                    break;
                }
            case WorldOp.FlipCube:
                {
                    var p = payload.Item6;
                    OctaEdit.FlipSelection(Octa, p.Sel.ToSelection());
                    break;
                }
            case WorldOp.RotateCube:
                {
                    var p = payload.Item7;
                    OctaEdit.RotateSelection(Octa, p.Sel.ToSelection(), p.Direction);
                    break;
                }
            case WorldOp.Remip:
                {
                    var p = payload.Item5;
                    LatestKeyframe = OctaEdit.RemipAll(Octa, p.MipVis, Entities, Environment);
                    break;
                }
            case WorldOp.NewMap:
                {
                    var p = payload.Item11;
                    Octa.EmptyMap(p.Scale);
                    break;
                }
            case WorldOp.AddEntity:
                {
                    var p = payload.Item8;
                    Entities.RemoveAll(e => e.Id == p.EntityId);
                    Entities.Add(p.ToMapEntity());
                    break;
                }
            case WorldOp.RemoveEntity:
                {
                    var p = payload.Item8;
                    Entities.RemoveAll(e => e.Id == p.EntityId);
                    break;
                }
            case WorldOp.EditEntity:
                {
                    var p = payload.Item8;
                    int idx = Entities.FindIndex(e => e.Id == p.EntityId);
                    if (idx >= 0)
                        Entities[idx] = p.ToMapEntity();
                    else
                        Entities.Add(p.ToMapEntity());
                    break;
                }
            case WorldOp.SetEnvironment:
                {
                    var p = payload.Item9;
                    Environment = p;
                    break;
                }
            default:
                throw new NotSupportedException($"Unhandled WorldOp {cmd.Op}");
        }
    }
}
