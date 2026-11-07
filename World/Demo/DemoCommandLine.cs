using System.CommandLine;
using SauerbratenRevival.World.Demo.Formats.Dmo;

namespace SauerbratenRevival.World.Demo;

/// <summary>
/// CLI argument parser for demo recording, playback, and converter flags using System.CommandLine.
/// Supports top-level options (--demo, --record, --map, --mode) and subcommands (demo info/convert/dump).
/// </summary>
public static class DemoCommandLine
{
    public static readonly Option<string?> DemoOption = new("--demo")
    {
        Description = "Replay a demo by file path, URI, or name."
    };

    public static readonly Option<string?> RecordOption = new("--record")
    {
        Description = "Record a demo to user://demos/ (optionally passing custom path).",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static readonly Option<string?> MapOption = new("--map")
    {
        Description = "Map to load (e.g. church51, base/church51.ogz)."
    };

    public static readonly Option<string?> ModeOption = new("--mode")
    {
        Description = "Game mode to play (e.g. ffa, ctf, effic_ctf)."
    };

    public static System.CommandLine.Command CreateCommand()
    {
        var cmd = new System.CommandLine.Command("sauerbraten", "Sauerbraten Revival");
        cmd.Add(DemoOption);
        cmd.Add(RecordOption);
        cmd.Add(MapOption);
        cmd.Add(ModeOption);

        // Subcommands for demo operations (info, convert, dump) under `demo`
        var demoSub = DmoConverter.CreateDemoSubcommand();
        cmd.Subcommands.Add(demoSub);

        // Also expose first-level subcommands directly on the root
        cmd.Subcommands.Add(DmoConverter.CreateInfoCommand());
        cmd.Subcommands.Add(DmoConverter.CreateConvertCommand());
        cmd.Subcommands.Add(DmoConverter.CreateDumpCommand());

        return cmd;
    }

    public sealed record ParsedDemoArgs(string? DemoPath, bool Record, string? RecordPath, string? Map, string? Mode);

    public static ParsedDemoArgs Parse(string[]? args)
    {
        if (args == null || args.Length == 0)
            return new ParsedDemoArgs(null, false, null, null, null);

        var cmd = CreateCommand();
        var parseResult = cmd.Parse(args);

        string? demo = parseResult.GetValue(DemoOption);
        var recordResult = parseResult.GetResult(RecordOption);
        bool record = recordResult != null;
        string? recordPath = parseResult.GetValue(RecordOption);
        string? map = parseResult.GetValue(MapOption);
        string? mode = parseResult.GetValue(ModeOption);

        return new ParsedDemoArgs(demo, record, recordPath, map, mode);
    }
}
