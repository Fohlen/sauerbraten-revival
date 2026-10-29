using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Commands;

namespace SauerbratenRevival.World.Runtime.Formats.Save;

/// <summary>
/// Godot <see cref="Resource"/> wrapper for the reference-based map history and
/// content-addressed object store (docs/adr/0008-reference-based-maps.md). Saved as <c>history.res</c>.
///
/// Holds the base map reference (<see cref="BaseRef"/>), HEAD commit ID (<see cref="HeadCommitId"/>),
/// linear commit chain (<see cref="CommitChain"/>), and delta objects (<see cref="Objects"/>).
/// </summary>
[GlobalClass]
public partial class HistoryResource : Resource
{
    /// <summary>Relative or res:// path to the base map directory (empty for base snapshots).</summary>
    [Export] public string BaseRef { get; set; } = "";

    /// <summary>Expected TreeId of the BaseRef map, verified on load to detect base map corruption.</summary>
    [Export] public string ExpectedBaseTreeId { get; set; } = "";

    /// <summary>The current HEAD commit hash (hex).</summary>
    [Export] public string HeadCommitId { get; set; } = "";

    /// <summary>Linear commit chain hashes from initial commit to HEAD.</summary>
    [Export] public Godot.Collections.Array<string> CommitChain { get; set; } = new();

    /// <summary>Content-addressed objects unique to this map (hex ObjectId -> byte payload).</summary>
    [Export] public Godot.Collections.Dictionary<string, byte[]> Objects { get; set; } = new();

    /// <summary>One encoded <see cref="Command"/> per entry, in sequence order (kept for backwards compatibility).</summary>
    [Export] public Godot.Collections.Array<byte[]> Commands { get; set; } = new();

    /// <summary>Script resource path, assigned so <see cref="ResourceSaver"/> writes a proper ext_resource reference.</summary>
    public const string ScriptPath = "res://World/Runtime/Formats/Save/HistoryResource.cs";

    public static HistoryResource FromCommands(IEnumerable<Command> commands)
    {
        var res = new HistoryResource();
        foreach (var c in commands) res.Commands.Add(CommandCodec.Encode(c));
        AttachScript(res);
        return res;
    }

    /// <summary>Decode every command, in order. Throws on a malformed entry.</summary>
    public List<Command> ToCommands()
    {
        if (Commands.Count > 0)
        {
            var list = new List<Command>(Commands.Count);
            foreach (var blob in Commands) list.Add(CommandCodec.Decode(blob));
            return list;
        }

        return new List<Command>();
    }

    private static void AttachScript(Resource res)
    {
        var script = GD.Load<Script>(ScriptPath);
        if (script != null) res.SetScript(script);
    }
}
