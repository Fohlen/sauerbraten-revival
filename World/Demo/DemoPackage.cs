using System;
using System.IO;
using FlatSharp;
using Godot;

namespace SauerbratenRevival.Format;

/// <summary>
/// Domain extension for FlatSharp-generated <see cref="PlayerUpdateCmd"/>.
/// Represents the captured player input and optional kinematic verification state for a 5 ms simulation tick.
/// </summary>
public partial class PlayerUpdateCmd
{
    public PlayerUpdateCmd(
        int tick,
        int move,
        int strafe,
        bool jumping,
        float yaw,
        float pitch,
        bool attacking,
        int selectedGun)
    {
        Tick = tick;
        Move = move;
        Strafe = strafe;
        Jumping = jumping;
        Yaw = yaw;
        Pitch = pitch;
        Attacking = attacking;
        SelectedGun = selectedGun;
    }

    public PlayerUpdateCmd(
        int tick,
        int move,
        int strafe,
        bool jumping,
        float yaw,
        float pitch,
        bool attacking,
        int selectedGun,
        Vector3 pos,
        Vector3 vel,
        byte physState,
        int lifeSequence)
        : this(tick, move, strafe, jumping, yaw, pitch, attacking, selectedGun)
    {
        Pos = new Vec3f { X = pos.X, Y = pos.Y, Z = pos.Z };
        Vel = new Vec3f { X = vel.X, Y = vel.Y, Z = vel.Z };
        PhysState = physState;
        LifeSequence = lifeSequence;
    }

    public override string ToString() =>
        $"PlayerUpdate[Tick={Tick}, Move={Move}, Strafe={Strafe}, Jump={Jumping}, Yaw={Yaw:F1}, Pitch={Pitch:F1}, Attack={Attacking}, Gun={SelectedGun}]";
}

/// <summary>
/// Domain methods and FlatSharp serialization for <see cref="DemoPackage"/>.
/// Replayable demo container containing a unified stream of <see cref="Command"/>s.
/// </summary>
public partial class DemoPackage
{
    /// <summary>Total duration of recorded updates in seconds (5 ms per tick).</summary>
    public float DurationSeconds => TotalTicks * (SauerbratenRevival.World.Physics.Phys.PhysFrameTime / 1000f);

    /// <summary>Total count of player update commands in the package.</summary>
    public int TotalTicks
    {
        get
        {
            if (Commands == null) return 0;
            int count = 0;
            for (int i = 0; i < Commands.Count; i++)
            {
                if (Commands[i].Op == WorldOp.PlayerUpdate) count++;
            }
            return count;
        }
    }

    /// <summary>Total count of all commands (movement, lifecycle, and edits) in the package.</summary>
    public int TotalCommands => Commands?.Count ?? 0;

    /// <summary>
    /// Encodes this demo package into a FlatBuffers binary buffer (*.fbs / *.bin).
    /// </summary>
    public byte[] Encode() => Encode(this);

    /// <summary>
    /// Encodes a <see cref="DemoPackage"/> into a FlatBuffers binary buffer.
    /// </summary>
    public static byte[] Encode(DemoPackage package)
    {
        byte[] buffer = new byte[Serializer.GetMaxSize(package)];
        int bytesWritten = Serializer.Write(buffer, package);
        return buffer.AsSpan(0, bytesWritten).ToArray();
    }

    /// <summary>
    /// Decodes a <see cref="DemoPackage"/> from a FlatBuffers binary buffer.
    /// </summary>
    public static DemoPackage Decode(byte[] bytes) => Serializer.Parse(bytes);

    /// <summary>Default directory where demos are stored (<c>user://demos</c>), matching Sauerbraten's demo/ folder.</summary>
    public static string DefaultDemoDir => SauerbratenRevival.World.Content.ContentRoots.ProjectDir(SauerbratenRevival.World.Content.ContentKind.Demo);

    /// <summary>
    /// Generates a demo file path in user://demos/ using Sauerbraten's timestamp naming schema:
    /// "YYYY-MM-DD_HH.mm.ss.fbs" (or optionally prefixed with the map name).
    /// </summary>
    public static string GenerateDemoPath(string? mapName = null, string extension = "fbs")
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH.mm.ss");
        string baseName = string.IsNullOrWhiteSpace(mapName)
            ? timestamp
            : $"{Path.GetFileNameWithoutExtension(mapName)}_{timestamp}";
        return $"{DefaultDemoDir}/{baseName}.{extension.TrimStart('.')}";
    }

    /// <summary>
    /// Resolves a demo path or file name through <see cref="SauerbratenRevival.World.Content.ContentRoots"/>.
    /// Searches direct path, user://demos/, res://assets/demos/, and the reference dump.
    /// </summary>
    public static string? ResolveDemoPath(string path) =>
        SauerbratenRevival.World.Content.ContentRoots.TryResolveDemo(path);

    /// <summary>
    /// Resolves a demo path through <see cref="SauerbratenRevival.World.Content.ContentRoots"/>, falling back
    /// to the canonical user://demos/ path if absent.
    /// </summary>
    public static string Resolve(string path) =>
        SauerbratenRevival.World.Content.ContentRoots.ResolveDemo(path);

    /// <summary>
    /// Saves the demo package to disk at the given path (*.fbs or *.bin).
    /// Supports Godot res:// or user:// paths as well as absolute filesystem paths.
    /// </summary>
    public static void Save(string path, DemoPackage package)
    {
        string globalPath = ProjectSettings.GlobalizePath(path);
        string? dir = Path.GetDirectoryName(globalPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        byte[] bytes = Encode(package);
        File.WriteAllBytes(globalPath, bytes);
    }

    /// <summary>
    /// Loads a demo package from disk (*.fbs or *.bin).
    /// </summary>
    public static DemoPackage Load(string path)
    {
        string targetPath = ResolveDemoPath(path) ?? path;
        string globalPath = ProjectSettings.GlobalizePath(targetPath);
        if (!File.Exists(globalPath))
        {
            throw new FileNotFoundException($"Demo file not found at '{path}' (resolved '{globalPath}')");
        }
        byte[] bytes = File.ReadAllBytes(globalPath);
        return Decode(bytes);
    }
}
