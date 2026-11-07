using System;
using System.CommandLine;
using System.IO;
using System.Text.Json;
using FlatSharp;
using SauerbratenRevival.Format;

namespace SauerbratenRevival.World.Demo.Formats.Dmo;

/// <summary>
/// CLI tool and converter for Sauerbraten .dmo demo files using System.CommandLine.
/// Provides info, convert (.dmo -> .fbs), and trajectory dump commands.
/// </summary>
public static class DmoConverter
{
    public static System.CommandLine.Command CreateCommand(TextWriter? outWriter = null, TextWriter? errorWriter = null)
    {
        var outW = outWriter ?? Console.Out;
        var errW = errorWriter ?? Console.Error;

        var root = new System.CommandLine.Command("dmoreader", "dmoreader — Sauerbraten .dmo demo reader & .fbs converter");

        // info command
        var infoCmd = new System.CommandLine.Command("info", "Print demo metadata, player scores, frags, and statistics.");
        var infoFileArg = new Argument<string>("file") { Description = "Path to .dmo demo file." };
        var infoJsonOpt = new Option<bool>("--json") { Description = "Output summary as JSON." };
        infoCmd.Add(infoFileArg);
        infoCmd.Add(infoJsonOpt);
        infoCmd.SetAction(parseResult =>
        {
            string file = parseResult.GetValue(infoFileArg)!;
            bool json = parseResult.GetValue(infoJsonOpt);
            return RunInfo(file, json, outW, errW);
        });

        // convert command
        var convertCmd = new System.CommandLine.Command("convert", "Convert .dmo into revival FlatBuffers .fbs demo package for engine playback.");
        var convertFileArg = new Argument<string>("file") { Description = "Path to .dmo demo file." };
        var outOpt = new Option<string?>("-o", "--output") { Description = "Output .fbs file path." };
        var noRemapOpt = new Option<bool>("--no-remap") { Description = "Do not remap active players to client 0." };
        var primaryCnOpt = new Option<int?>("--primary-cn") { Description = "Explicit client number to treat as primary." };
        convertCmd.Add(convertFileArg);
        convertCmd.Add(outOpt);
        convertCmd.Add(noRemapOpt);
        convertCmd.Add(primaryCnOpt);
        convertCmd.SetAction(parseResult =>
        {
            string file = parseResult.GetValue(convertFileArg)!;
            string? output = parseResult.GetValue(outOpt);
            bool noRemap = parseResult.GetValue(noRemapOpt);
            int? primaryCn = parseResult.GetValue(primaryCnOpt);
            return RunConvert(file, output, !noRemap, primaryCn, outW, errW);
        });

        // dump command
        var dumpCmd = new System.CommandLine.Command("dump", "Dump trajectory position updates as CSV.");
        var dumpFileArg = new Argument<string>("file") { Description = "Path to .dmo demo file." };
        var playerOpt = new Option<int?>("--player") { Description = "Filter by player client number." };
        var limitOpt = new Option<int>("--limit")
        {
            Description = "Maximum number of position records to dump.",
            DefaultValueFactory = _ => 20
        };
        dumpCmd.Add(dumpFileArg);
        dumpCmd.Add(playerOpt);
        dumpCmd.Add(limitOpt);
        dumpCmd.SetAction(parseResult =>
        {
            string file = parseResult.GetValue(dumpFileArg)!;
            int? player = parseResult.GetValue(playerOpt);
            int limit = parseResult.GetValue(limitOpt);
            return RunDump(file, player, limit, outW, errW);
        });

        root.Add(infoCmd);
        root.Add(convertCmd);
        root.Add(dumpCmd);

        return root;
    }

    public static int Run(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var outW = stdout ?? Console.Out;
        var errW = stderr ?? Console.Error;

        if (args == null || args.Length == 0)
        {
            var cmd = CreateCommand(outW, errW);
            cmd.Parse("-h").Invoke();
            return 0;
        }

        // Direct file invocation shorthand: e.g. "dmoreader demo.dmo" or "dmoreader demo.dmo --json"
        string first = args[0];
        if (first is not "info" and not "convert" and not "dump" and not "-h" and not "--help" and not "-?" and not "/?")
        {
            if (File.Exists(first) || first.EndsWith(".dmo", StringComparison.OrdinalIgnoreCase))
            {
                var newArgs = new string[args.Length + 1];
                newArgs[0] = "info";
                Array.Copy(args, 0, newArgs, 1, args.Length);
                args = newArgs;
            }
        }

        var root = CreateCommand(outW, errW);
        return root.Parse(args).Invoke();
    }

    public static int RunInfo(string filePath, bool json, TextWriter outW, TextWriter errW)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            errW.WriteLine($"Error: Please specify a valid .dmo file path (file not found: '{filePath}').");
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
            outW.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            outW.WriteLine($"Demo: {Path.GetFileName(filePath)}");
            outW.WriteLine($"Map: {dmo.MapName} (Mode: {dmo.GameModeName} [{dmo.GameMode}])");
            outW.WriteLine($"Duration: {dmo.DurationMillis / 1000}s ({dmo.DurationMillis}ms), Records: {dmo.TotalRecords}");
            outW.WriteLine($"Positions: {dmo.Positions.Count}, Shots: {dmo.Shots.Count}, Deaths: {dmo.Deaths.Count}");
            outW.WriteLine();
            outW.WriteLine("Players:");
            foreach (var (_, p) in dmo.Players)
            {
                if (p.PositionCount > 0 || p.Shots > 0 || p.Frags > 0 || p.Deaths > 0)
                {
                    outW.WriteLine($"  Client {p.ClientNum}: '{p.Name}' (Team: {p.Team}) " +
                                  $"Frags: {p.Frags}, Deaths: {p.Deaths}, Shots: {p.Shots}, Positions: {p.PositionCount}");
                }
            }
        }
        return 0;
    }

    public static int RunConvert(string filePath, string? outPath, bool remapPrimary, int? primaryCn, TextWriter outW, TextWriter errW)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            errW.WriteLine($"Error: Please specify a valid .dmo file path (file not found: '{filePath}').");
            return 1;
        }

        outPath ??= Path.ChangeExtension(filePath, ".fbs");

        outW.WriteLine($"Reading {filePath}...");
        var dmo = DmoReader.ReadFile(filePath);

        outW.WriteLine($"Translating to DemoPackage (Map: {dmo.MapName}, Mode: {dmo.GameModeName}, Duration: {dmo.DurationMillis / 1000}s)...");
        var package = DmoReader.ToDemoPackage(dmo, remapActivePlayers: remapPrimary, primaryCn: primaryCn);

        byte[] buffer = new byte[DemoPackage.Serializer.GetMaxSize(package)];
        int bytesWritten = DemoPackage.Serializer.Write(buffer.AsSpan(), package);
        byte[] trimmed = buffer.AsSpan(0, bytesWritten).ToArray();

        string? dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(outPath, trimmed);

        outW.WriteLine($"Successfully converted: {outPath}");
        outW.WriteLine($"  Total Commands: {package.Commands?.Count ?? 0}");
        outW.WriteLine($"  Package Size:   {trimmed.Length / 1024.0:F1} KB");
        return 0;
    }

    public static int RunDump(string filePath, int? playerCn, int limit, TextWriter outW, TextWriter errW)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            errW.WriteLine($"Error: Please specify a valid .dmo file path (file not found: '{filePath}').");
            return 1;
        }

        var dmo = DmoReader.ReadFile(filePath);
        outW.WriteLine("Timestamp(ms),Tick,ClientNum,X,Y,Z,Yaw,Pitch,Move,Strafe,PhysState");
        int count = 0;
        foreach (var pos in dmo.Positions)
        {
            if (playerCn.HasValue && pos.ClientNum != playerCn.Value) continue;
            outW.WriteLine($"{pos.Millis},{pos.Millis / 5},{pos.ClientNum},{pos.Pos.X:F3},{pos.Pos.Y:F3},{pos.Pos.Z:F3},{pos.Yaw:F1},{pos.Pitch:F1},{pos.Move},{pos.Strafe},{pos.PhysState}");
            count++;
            if (count >= limit) break;
        }
        return 0;
    }
}
