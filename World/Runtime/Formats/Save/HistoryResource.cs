using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Commands;

namespace SauerbratenRevival.World.Runtime.Formats.Save;

/// <summary>
/// Godot <see cref="Resource"/> wrapper for the command history since the base
/// snapshot (docs/editor-and-commands.md §7). Saved as <c>history.res</c>.
///
/// The history is a sequence of canonical command blobs (<see cref="WorldCommandCodec"/>),
/// each self-describing, so a newer build can skip an unknown op tail rather than
/// fail the whole file (§7.3).
///
/// The class name must match this filename for Godot's C# script resolution (see
/// <see cref="WorldResource.AttachScript"/>).
/// </summary>
[GlobalClass]
public partial class HistoryResource : Resource
{
    /// <summary>One encoded <see cref="WorldCommand"/> per entry, in sequence order.</summary>
    [Export] public Godot.Collections.Array<byte[]> Commands { get; set; } = new();

    /// <summary>Script resource path, assigned so <see cref="ResourceSaver"/> writes a proper ext_resource reference.</summary>
    public const string ScriptPath = "res://World/Runtime/Formats/Save/HistoryResource.cs";

    public static HistoryResource FromCommands(IEnumerable<WorldCommand> commands)
    {
        var res = new HistoryResource();
        foreach (var c in commands) res.Commands.Add(WorldCommandCodec.Encode(c));
        AttachScript(res);
        return res;
    }

    /// <summary>Decode every command, in order. Throws on a malformed entry.</summary>
    public List<WorldCommand> ToCommands()
    {
        var list = new List<WorldCommand>(Commands.Count);
        foreach (var blob in Commands) list.Add(WorldCommandCodec.Decode(blob));
        return list;
    }

    private static void AttachScript(Resource res)
    {
        var script = GD.Load<Script>(ScriptPath);
        if (script != null) res.SetScript(script);
    }
}
