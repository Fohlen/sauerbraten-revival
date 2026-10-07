using System.Collections.Generic;

namespace SauerbratenRevival.World.Commands;

/// <summary>
/// One applied command plus the local state needed to undo it. The undo entry is
/// **local only** — never transmitted (docs/editor-and-commands.md §5.5). Its shape
/// depends on the op: destructive ops carry a captured pre-state block; reversible
/// ops carry nothing (the inverse is an op swap).
/// </summary>
public sealed class LogEntry
{
    public EditCommand Command = null!;

    /// <summary>
    /// Pre-state captured before the command was applied, for ops whose inverse is
    /// "restore this region". Null for ops that reverse by a symmetric op.
    /// </summary>
    public OctaEdit.CapturedBlock? UndoBlock;

    /// <summary>
    /// The region the undo block covers. For <see cref="EditOp.ReplaceBlock"/> this
    /// is the target; for the destructive selection ops it is the selection.
    /// </summary>
    public Block3 UndoRegion;

    /// <summary>Redo state: for <see cref="EditOp.ReplaceBlock"/>, the block that was written.</summary>
    public OctaEdit.CapturedBlock? RedoBlock;
}

/// <summary>
/// Append-only command log plus this client's undo/redo stacks.
///
/// The log is the source of truth (docs §2); the octree is a materialized view.
/// Undo/redo follow the shared-world rules (§5.5): only the local peer's commands
/// are undoable, and a rejected/conflicted undo is discarded rather than merged.
/// This class is offline — networking wraps it (M6).
/// </summary>
public sealed class WorldCommandLog
{
    private readonly List<LogEntry> _entries = new();
    private readonly List<LogEntry> _undone = new();
    private ulong _nextSequence;

    /// <summary>The issuing peer (0 = local single-player).</summary>
    public int LocalPeerId { get; }

    /// <summary>All applied entries, in order.</summary>
    public IReadOnlyList<LogEntry> Entries => _entries;

    /// <summary>This peer's own applied entries, in order (the undo candidates).</summary>
    public IEnumerable<LogEntry> OwnEntries()
    {
        foreach (var e in _entries)
            if (e.Command.AuthorPeerId == LocalPeerId) yield return e;
    }

    public WorldCommandLog(int localPeerId = 0) => LocalPeerId = localPeerId;

    /// <summary>Assign the next sequence number to a command (caller then applies it).</summary>
    public ulong NextSequence() => _nextSequence;

    /// <summary>Record an applied command and advance the sequence. Clears the redo stack.</summary>
    public LogEntry Record(EditCommand cmd)
    {
        cmd.Sequence = _nextSequence++;
        var entry = new LogEntry { Command = cmd };
        _entries.Add(entry);
        _undone.Clear(); // a new edit invalidates redo
        return entry;
    }

    /// <summary>The most recent action of the given peer: the next undo candidate (own commands only).</summary>
    public LogEntry? PeekUndo()
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
            if (_entries[i].Command.AuthorPeerId == LocalPeerId) return _entries[i];
        return null;
    }

    /// <summary>The most recently undone entry: the redo candidate.</summary>
    public LogEntry? PeekRedo() => _undone.Count > 0 ? _undone[^1] : null;

    /// <summary>
    /// Pop the undo candidate off the applied log. The caller must have already
    /// reverted the world (the log does not touch the octree itself).
    /// </summary>
    public LogEntry? PopForUndo()
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].Command.AuthorPeerId != LocalPeerId) continue;
            var entry = _entries[i];
            _entries.RemoveAt(i);
            _undone.Add(entry);
            return entry;
        }
        return null;
    }

    /// <summary>Pop the redo candidate; the caller re-applies it, then calls <see cref="Record"/>.</summary>
    public LogEntry? PopForRedo()
    {
        if (_undone.Count == 0) return null;
        var entry = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        return entry;
    }

    /// <summary>
    /// Discard an undo candidate without reverting (used when the undo is rejected
    /// — §5.5 Rule 3: a rejected undo is dropped from the stack, not retried).
    /// </summary>
    public LogEntry? DiscardForUndo()
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].Command.AuthorPeerId != LocalPeerId) continue;
            var entry = _entries[i];
            _entries.RemoveAt(i);
            return entry;
        }
        return null;
    }

    /// <summary>Append an already-sequenced remote command (does not affect redo).</summary>
    public LogEntry RecordRemote(EditCommand cmd)
    {
        var entry = new LogEntry { Command = cmd };
        _entries.Add(entry);
        return entry;
    }
}
