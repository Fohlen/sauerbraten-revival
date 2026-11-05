using System;
using System.IO;
using System.Text.Json;
using FlatSharp;
using SauerbratenRevival.Format;

namespace SauerbratenRevival.Tools.DmoReader;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        string[] cmdArgs = args.Length > 1 ? args[1..] : Array.Empty<string>();

        try
        {
            return command switch
            {
                "info" => RunInfo(cmdArgs),
                "convert" => RunConvert(cmdArgs),
                "dump" => RunDump(cmdArgs),
                _ => HandleDirectFileOrUnknown(command, cmdArgs)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("dmoreader — Sauerbraten .dmo demo reader & .fbs converter");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dmoreader info <demo.dmo> [--json]");
        Console.WriteLine("  dmoreader convert <demo.dmo> [-o <out.fbs>] [--no-remap] [--primary-cn <N>]");
        Console.WriteLine("  dmoreader dump <demo.dmo> [--player <cn>] [--limit <N>]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  info     Print demo metadata, player scores, frags, and statistics.");
        Console.WriteLine("  convert  Convert .dmo into revival FlatBuffers .fbs demo package for engine playback.");
        Console.WriteLine("  dump     Dump trajectory position updates as CSV.");
    }

    private static int HandleDirectFileOrUnknown(string firstArg, string[] remaining)
    {
        if (File.Exists(firstArg))
        {
            return RunInfo(new[] { firstArg });
        }
        Console.Error.WriteLine($"Unknown command or file not found: '{firstArg}'");
        PrintUsage();
        return 1;
    }

    private static int RunInfo(string[] args)
    {
        string? filePath = null;
        bool json = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--json") json = true;
            else if (!args[i].StartsWith('-')) filePath = args[i];
        }

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            Console.Error.WriteLine("Error: Please specify a valid .dmo file path.");
            return 1;
        }

        var dmo = DmoReader.ReadFile(filePath);

        if (json)
        {
            var summary = new
            {
                magic = dmo.Magic,
                version = dmo.Version,
                protocol = dmo.Protocol,
                map = dmo.MapName,
                gamemode = dmo.GameMode,
                gamemode_name = dmo.GameModeName,
                duration_millis = dmo.DurationMillis,
                duration_seconds = dmo.DurationMillis / 1000.0,
                total_records = dmo.TotalRecords,
                total_positions = dmo.Positions.Count,
                total_shots = dmo.Shots.Count,
                total_deaths = dmo.Deaths.Count,
                players = dmo.Players.Values
            };
            Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"Demo: {Path.GetFileName(filePath)}");
            Console.WriteLine($"Map: {dmo.MapName} (Mode: {dmo.GameModeName} [{dmo.GameMode}])");
            Console.WriteLine($"Duration: {dmo.DurationMillis / 1000}s ({dmo.DurationMillis}ms), Records: {dmo.TotalRecords}");
            Console.WriteLine($"Positions: {dmo.Positions.Count}, Shots: {dmo.Shots.Count}, Deaths: {dmo.Deaths.Count}");
            Console.WriteLine();
            Console.WriteLine("Players:");
            foreach (var (_, p) in dmo.Players)
            {
                if (p.PositionCount > 0 || p.Shots > 0 || p.Frags > 0 || p.Deaths > 0)
                {
                    Console.WriteLine($"  Client {p.ClientNum}: '{p.Name}' (Team: {p.Team}) " +
                                      $"Frags: {p.Frags}, Deaths: {p.Deaths}, Shots: {p.Shots}, Positions: {p.PositionCount}");
                }
            }
        }
        return 0;
    }

    private static int RunConvert(string[] args)
    {
        string? filePath = null;
        string? outPath = null;
        bool remapPrimary = true;
        int? primaryCn = null;

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "-o" || args[i] == "--output") && i + 1 < args.Length)
            {
                outPath = args[++i];
            }
            else if (args[i] == "--no-remap")
            {
                remapPrimary = false;
            }
            else if (args[i] == "--primary-cn" && i + 1 < args.Length)
            {
                primaryCn = int.Parse(args[++i]);
            }
            else if (!args[i].StartsWith('-'))
            {
                filePath = args[i];
            }
        }

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            Console.Error.WriteLine("Error: Please specify a valid .dmo file path.");
            return 1;
        }

        outPath ??= Path.ChangeExtension(filePath, ".fbs");

        Console.WriteLine($"Reading {filePath}...");
        var dmo = DmoReader.ReadFile(filePath);

        Console.WriteLine($"Translating to DemoPackage (Map: {dmo.MapName}, Mode: {dmo.GameModeName}, Duration: {dmo.DurationMillis / 1000}s)...");
        var package = DmoReader.ToDemoPackage(dmo, remapActivePlayers: remapPrimary, primaryCn: primaryCn);

        byte[] buffer = new byte[DemoPackage.Serializer.GetMaxSize(package)];
        int bytesWritten = DemoPackage.Serializer.Write(buffer, package);
        byte[] trimmed = buffer.AsSpan(0, bytesWritten).ToArray();

        string? dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(outPath, trimmed);

        Console.WriteLine($"Successfully converted: {outPath}");
        Console.WriteLine($"  Total Commands: {package.Commands?.Count ?? 0}");
        Console.WriteLine($"  Package Size:   {trimmed.Length / 1024.0:F1} KB");
        return 0;
    }

    private static int RunDump(string[] args)
    {
        string? filePath = null;
        int? playerCn = null;
        int limit = 20;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--player" && i + 1 < args.Length)
            {
                playerCn = int.Parse(args[++i]);
            }
            else if (args[i] == "--limit" && i + 1 < args.Length)
            {
                limit = int.Parse(args[++i]);
            }
            else if (!args[i].StartsWith('-'))
            {
                filePath = args[i];
            }
        }

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            Console.Error.WriteLine("Error: Please specify a valid .dmo file path.");
            return 1;
        }

        var dmo = DmoReader.ReadFile(filePath);
        Console.WriteLine("Timestamp(ms),Tick,ClientNum,X,Y,Z,Yaw,Pitch,Move,Strafe,PhysState");
        int count = 0;
        foreach (var pos in dmo.Positions)
        {
            if (playerCn.HasValue && pos.ClientNum != playerCn.Value) continue;
            Console.WriteLine($"{pos.Millis},{pos.Millis / 5},{pos.ClientNum},{pos.Pos.X:F3},{pos.Pos.Y:F3},{pos.Pos.Z:F3},{pos.Yaw:F1},{pos.Pitch:F1},{pos.Move},{pos.Strafe},{pos.PhysState}");
            count++;
            if (count >= limit) break;
        }
        return 0;
    }
}
