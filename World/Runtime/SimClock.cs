using Godot;

namespace SauerbratenRevival.World.Runtime;

/// <summary>
/// The single simulation clock for a world (engine <c>lastmillis</c>). Advanced only
/// by the world's fixed 5&#160;ms physics step and read by every consumer (players,
/// items, flags, HUD), so all gameplay timestamps share one time base instead of each
/// system running its own accumulator or falling back to Godot's wall clock.
///
/// It is a Godot <see cref="Resource"/> so it can be exported, saved to a
/// <c>.tres</c>, inspected in the editor, and assigned per scene/session. It is
/// deliberately <b>not</b> a process-wide autoload: the project is
/// server-authoritative and session-scoped, so each world/session owns its own
/// clock (a global <c>lastmillis</c> would break as soon as one process hosts two
/// sessions).
///
/// The clock is <b>polled</b> via <see cref="NowMillis"/>, not signalled per tick:
/// <see cref="Advance"/> runs at the fixed physics rate, so emitting <c>changed</c>
/// on every advance would be a per-frame storm. Only an explicit <see cref="Set"/>
/// (bootstrap or override) emits <c>changed</c>.
/// </summary>
[GlobalClass]
public partial class SimClock : Resource
{
    /// <summary>Script resource path, so a code-created clock saves with a proper <c>ext_resource</c> reference.</summary>
    public const string ScriptPath = "res://World/SimClock.cs";

    /// <summary>Current simulation time in milliseconds (engine <c>lastmillis</c>).</summary>
    [Export] public int NowMillis { get; set; }

    public SimClock() { }

    public SimClock(int startMillis) => NowMillis = startMillis;

    /// <summary>Advance by one fixed physics step.</summary>
    public void Advance(int millis) => NowMillis += millis;

    /// <summary>Seed or override the clock, emitting <c>changed</c> for listeners.</summary>
    public void Set(int millis)
    {
        NowMillis = millis;
        EmitChanged();
    }

    /// <summary>Create a clock that carries its script, so <see cref="ResourceSaver"/> can persist it.</summary>
    public static SimClock At(int startMillis)
    {
        var clock = new SimClock(startMillis);
        var script = GD.Load<Script>(ScriptPath);
        if (script != null) clock.SetScript(script);
        return clock;
    }
}
