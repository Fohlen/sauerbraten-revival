global using KeyframePackage = SauerbratenRevival.Format.KeyframePackage;
global using ChunkKeyframe = SauerbratenRevival.Format.ChunkKeyframe;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FlatSharp;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.Format;

/// <summary>
/// Domain methods and serialization helpers extending the FlatSharp-generated <see cref="KeyframePackage"/>
/// and <see cref="ChunkKeyframe"/> tables (ADR 0008).
/// </summary>
public partial class KeyframePackage
{
    public const byte FlagChildren = 1 << 0;
    public const byte FlagMaterial = 1 << 1;
    public const byte FlagMerged = 1 << 2;
    public const byte FlagVisible = 1 << 3;
    public const byte FlagSurface = 1 << 4;

    /// <summary>
    /// Serializes a single root octant cube (0..7) and its subtree into a <see cref="ChunkKeyframe"/>
    /// using dense pre-order traversal (ADR 0008 §3.B).
    /// </summary>
    public static ChunkKeyframe SerializeChunk(Cube rootCube, byte chunkIndex)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        uint nodeCount = 0;
        WriteCube(w, rootCube, ref nodeCount);
        w.Flush();
        byte[] data = ms.ToArray();
        return new ChunkKeyframe
        {
            ChunkIndex = chunkIndex,
            UncompressedSize = (uint)data.Length,
            NodeCount = nodeCount,
            Data = data
        };
    }

    /// <summary>
    /// Deserializes a <see cref="ChunkKeyframe"/>'s dense pre-order stream into a <see cref="Cube"/> hierarchy.
    /// </summary>
    public static Cube DeserializeChunk(ChunkKeyframe chunk)
    {
        if (chunk.Data == null || chunk.Data.Value.Length == 0)
            throw new InvalidDataException($"Chunk {chunk.ChunkIndex} data is empty.");

        using var ms = new MemoryStream(chunk.Data.Value.ToArray(), writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        uint nodeCount = 0;
        return ReadCube(r, ref nodeCount);
    }

    /// <summary>
    /// Captures a complete <see cref="KeyframePackage"/> from an <see cref="Octa"/>, serializing
    /// all 8 root chunks and attaching entities, environment, git commit, and parent hash.
    /// </summary>
    public static KeyframePackage Capture(
        Octa octa,
        IEnumerable<MapEntity>? entities = null,
        SetEnvironmentCmd? env = null,
        uint keyframeIndex = 0,
        byte[]? baseSnapshotHash = null,
        string? gitCommit = null)
    {
        var chunks = new List<ChunkKeyframe>(8);
        for (byte i = 0; i < 8; i++)
        {
            chunks.Add(SerializeChunk(octa.WorldRoot[i], i));
        }

        var entityCmds = new List<EntityCmd>();
        if (entities != null)
        {
            foreach (var e in entities)
                entityCmds.Add(e.ToEntityCmd());
        }

        return new KeyframePackage
        {
            KeyframeIndex = keyframeIndex,
            FormatVersion = 1,
            WorldScale = octa.WorldScale,
            GitCommit = gitCommit ?? GitCommitResolver.GetHeadCommit(),
            BaseSnapshotHash = baseSnapshotHash ?? Array.Empty<byte>(),
            Chunks = chunks,
            Entities = entityCmds,
            Environment = env
        };
    }

    /// <summary>
    /// Computes the 32-byte SHA-256 hash of this keyframe package.
    /// </summary>
    public byte[] ComputeHash() => ComputeHash(this);

    public static byte[] ComputeHash(KeyframePackage keyframe)
    {
        byte[] buffer = Encode(keyframe);
        return SHA256.HashData(buffer);
    }

    public byte[] Encode() => Encode(this);

    public static byte[] Encode(KeyframePackage keyframe)
    {
        byte[] buffer = new byte[Serializer.GetMaxSize(keyframe)];
        int bytesWritten = Serializer.Write(buffer, keyframe);
        return buffer.AsSpan(0, bytesWritten).ToArray();
    }

    public static KeyframePackage Decode(byte[] bytes) => Serializer.Parse(bytes);

    /// <summary>
    /// Materializes a new <see cref="Octa"/> from the 8 chunk keyframes.
    /// </summary>
    public Octa ToOcta()
    {
        if (Chunks == null || Chunks.Count != 8)
            throw new InvalidDataException($"Expected 8 chunks in keyframe, got {Chunks?.Count ?? 0}");

        var roots = new Cube[8];
        foreach (var chunk in Chunks)
        {
            if (chunk.ChunkIndex >= 8)
                throw new InvalidDataException($"Invalid chunk index {chunk.ChunkIndex}");
            roots[chunk.ChunkIndex] = DeserializeChunk(chunk);
        }

        int allocNodes = CountAllocNodes(roots);
        var octa = new Octa(WorldScale, 1 << WorldScale, roots, allocNodes);
        OctaMerges.CalcMerges(octa);
        OctaTJoints.FindTJoints(octa);
        return octa;
    }

    /// <summary>
    /// Applies this keyframe package to an existing <see cref="Octa"/> and optional entity list.
    /// </summary>
    public void ApplyTo(Octa octa, List<MapEntity>? entities = null)
    {
        if (Chunks == null || Chunks.Count != 8)
            throw new InvalidDataException($"Expected 8 chunks in keyframe, got {Chunks?.Count ?? 0}");

        var roots = new Cube[8];
        foreach (var chunk in Chunks)
        {
            if (chunk.ChunkIndex >= 8)
                throw new InvalidDataException($"Invalid chunk index {chunk.ChunkIndex}");
            roots[chunk.ChunkIndex] = DeserializeChunk(chunk);
        }

        int allocNodes = CountAllocNodes(roots);
        octa.LoadRoot(roots, allocNodes, WorldScale);
        OctaMerges.CalcMerges(octa);
        OctaTJoints.FindTJoints(octa);

        if (entities != null)
        {
            entities.Clear();
            if (Entities != null)
            {
                foreach (var e in Entities)
                    entities.Add(e.ToMapEntity());
            }
        }
    }

    /// <summary>
    /// Applies this keyframe package to a <see cref="CommandSink"/>, restoring geometry, entities, and environment.
    /// </summary>
    public void ApplyTo(CommandSink sink)
    {
        ApplyTo(sink.Octa, sink.Entities);
        if (Environment != null)
        {
            sink.Environment = Environment;
        }
    }

    private static int CountAllocNodes(Cube[] cubes)
    {
        int count = 1;
        foreach (var c in cubes)
        {
            if (c.Children != null)
                count += CountAllocNodes(c.Children);
        }
        return count;
    }

    private static void WriteCube(BinaryWriter w, Cube c, ref uint nodeCount)
    {
        nodeCount++;
        byte flags = 0;
        if (c.Children != null) flags |= FlagChildren;
        if (c.Material != Octa.MAT_AIR) flags |= FlagMaterial;
        if (c.Merged != 0) flags |= FlagMerged;
        if (c.Visible != 0) flags |= FlagVisible;
        if (c.Ext != null) flags |= FlagSurface;
        w.Write(flags);

        w.Write(c.Faces[0]);
        w.Write(c.Faces[1]);
        w.Write(c.Faces[2]);
        for (int i = 0; i < 6; i++) w.Write(c.Texture[i]);
        if ((flags & FlagMaterial) != 0) w.Write(c.Material);
        if ((flags & FlagMerged) != 0) w.Write(c.Merged);
        if ((flags & FlagVisible) != 0) w.Write(c.Visible);
        if ((flags & FlagSurface) != 0) WriteSurfaces(w, c.Ext!);

        if ((flags & FlagChildren) != 0)
        {
            for (int i = 0; i < 8; i++)
                WriteCube(w, c.Children![i], ref nodeCount);
        }
    }

    private static Cube ReadCube(BinaryReader r, ref uint nodeCount)
    {
        nodeCount++;
        var c = new Cube();
        byte flags = r.ReadByte();
        c.Faces[0] = r.ReadUInt32();
        c.Faces[1] = r.ReadUInt32();
        c.Faces[2] = r.ReadUInt32();
        for (int i = 0; i < 6; i++) c.Texture[i] = r.ReadUInt16();
        if ((flags & FlagMaterial) != 0) c.Material = r.ReadUInt16();
        if ((flags & FlagMerged) != 0) c.Merged = r.ReadByte();
        if ((flags & FlagVisible) != 0) c.Visible = r.ReadByte();
        if ((flags & FlagSurface) != 0) c.Ext = ReadSurfaces(r);

        if ((flags & FlagChildren) != 0)
        {
            c.Children = new Cube[8];
            for (int i = 0; i < 8; i++)
                c.Children[i] = ReadCube(r, ref nodeCount);
        }
        return c;
    }

    private static void WriteSurfaces(BinaryWriter w, CubeExt ext)
    {
        for (int f = 0; f < 6; f++)
        {
            var s = ext.Surfaces[f];
            w.Write(s.Lmid0);
            w.Write(s.Lmid1);
            w.Write(s.Verts);
            w.Write(s.NumVerts);
        }
        var verts = ext.Verts ?? Array.Empty<VertInfo>();
        w.Write((ushort)verts.Length);
        foreach (var v in verts)
        {
            w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
            w.Write(v.U); w.Write(v.V); w.Write(v.Norm);
        }
    }

    private static CubeExt ReadSurfaces(BinaryReader r)
    {
        var ext = new CubeExt();
        for (int f = 0; f < 6; f++)
        {
            ext.Surfaces[f] = new SurfaceInfo
            {
                Lmid0 = r.ReadByte(),
                Lmid1 = r.ReadByte(),
                Verts = r.ReadByte(),
                NumVerts = r.ReadByte(),
            };
        }
        int count = r.ReadUInt16();
        var verts = new VertInfo[count];
        for (int i = 0; i < count; i++)
        {
            verts[i] = new VertInfo
            {
                X = r.ReadUInt16(),
                Y = r.ReadUInt16(),
                Z = r.ReadUInt16(),
                U = r.ReadUInt16(),
                V = r.ReadUInt16(),
                Norm = r.ReadUInt16(),
            };
        }
        ext.Verts = verts;
        return ext;
    }
}

/// <summary>
/// Queries the current repository Git commit SHA for drift detection and keyframe pegging (ADR 0008 §4).
/// </summary>
public static class GitCommitResolver
{
    public static string GetHeadCommit()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                string output = process.StandardOutput.ReadToEnd().Trim();
                if (process.WaitForExit(1000) && process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    return output;
                }
            }
        }
        catch
        {
            // Git unavailable or execution failed
        }

        return "";
    }
}
