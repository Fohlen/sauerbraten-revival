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

    public MapObjectStore ToStore(MapObjectStore? baseStore = null)
    {
        var store = new MapObjectStore { BaseStore = baseStore };
        foreach (var (k, v) in Objects)
        {
            if (ObjectId.TryParse(k, out var id))
                store.Put(id, v);
        }
        return store;
    }

    public static HistoryResource FromStore(MapObjectStore store, string headCommitId,
        IEnumerable<string> commitChain, string baseRef = "", string expectedBaseTreeId = "")
    {
        var res = new HistoryResource
        {
            BaseRef = baseRef,
            ExpectedBaseTreeId = expectedBaseTreeId,
            HeadCommitId = headCommitId,
        };
        foreach (var c in commitChain) res.CommitChain.Add(c);
        foreach (var (k, v) in store.Objects) res.Objects[k.ToString()] = v;
        AttachScript(res);
        return res;
    }

    public static HistoryResource FromCommands(IEnumerable<Command> commands)
    {
        var res = new HistoryResource();
        foreach (var c in commands) res.Commands.Add(CommandCodec.Encode(c));
        AttachScript(res);
        return res;
    }

    /// <summary>Decode every command, in order. Throws on a malformed entry.</summary>
    public List<Command> ToCommands(MapObjectStore? baseStore = null)
    {
        if (Commands.Count > 0)
        {
            var list = new List<Command>(Commands.Count);
            foreach (var blob in Commands) list.Add(CommandCodec.Decode(blob));
            return list;
        }

        // If stored as commits in object store:
        if (!string.IsNullOrEmpty(HeadCommitId) && ObjectId.TryParse(HeadCommitId, out var headId))
        {
            var store = ToStore(baseStore);
            var allCommands = new List<Command>();
            foreach (var cHex in CommitChain)
            {
                if (ObjectId.TryParse(cHex, out var cId))
                {
                    if (store.Has(cId))
                    {
                        var commit = store.GetCommit(cId);
                        if (!commit.CommandsId.IsEmpty && store.Has(commit.CommandsId))
                            allCommands.AddRange(store.GetCommands(commit.CommandsId));
                    }
                }
            }
            return allCommands;
        }

        return new List<Command>();
    }

    private static void AttachScript(Resource res)
    {
        var script = GD.Load<Script>(ScriptPath);
        if (script != null) res.SetScript(script);
    }
}
