using System;
using Godot;

namespace SauerbratenRevival.World.Runtime.Formats.Save;

/// <summary>
/// Godot <see cref="Resource"/> wrapper for the canonical world snapshot
/// (docs/editor-and-commands.md §7). Saved as <c>world.res</c> with
/// <see cref="ResourceSaver"/>; the blob inside is byte-stable so the archive hash
/// (and multiplayer parity) is meaningful regardless of Godot's Resource framing.
///
/// The Resource is a thin holder on purpose: all serialization logic lives in
/// <see cref="WorldSnapshot"/>, which is testable headless without Godot's
/// Resource subsystem.
///
/// The class name must match this filename for Godot's C# script resolution (see
/// <see cref="AttachScript"/>).
/// </summary>
[GlobalClass]
public partial class WorldResource : Resource
{
    /// <summary>The encoded snapshot blob (see <see cref="WorldSnapshot.Encode"/>).</summary>
    [Export] public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>Script resource path, assigned so <see cref="ResourceSaver"/> writes a proper ext_resource reference.</summary>
    public const string ScriptPath = "res://World/Runtime/Formats/Save/WorldResource.cs";

    public static WorldResource FromSnapshot(WorldSnapshot snapshot)
    {
        var res = new WorldResource { Data = snapshot.Encode() };
        AttachScript(res);
        return res;
    }

    public WorldSnapshot ToSnapshot() => WorldSnapshot.Decode(Data);

    /// <summary>
    /// Attach this class's C# script to a resource created in code. Without it,
    /// <see cref="ResourceSaver"/> embeds an unusable <c>local://CSharpScript_*</c>
    /// placeholder and loading yields a bare <see cref="Resource"/> (the class cannot
    /// be resolved). Attaching the script by path makes the save write an
    /// <c>ext_resource</c> reference instead.
    /// </summary>
    internal static void AttachScript(Resource res)
    {
        var script = GD.Load<Script>(ScriptPath);
        if (script != null) res.SetScript(script);
    }
}
