using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// Unified Flag and CTF state machine managing flags, hold spawns, carrier tracking,
/// scoring, timeouts, and rules for CTF, Protect, and Hold modes.
/// Port of <c>fpsgame/ctf.h</c> (r6889).
/// </summary>
public sealed class FlagManager
{
    public const int BaseRadius = 64;
    public const int BaseHeight = 24;
    public const int MaxFlags = 20;
    public const int FlagRadius = 16;
    public const int FlagLimit = 10;
    public const int MaxHoldSpawns = 100;
    public const int HoldSecs = 20;
    public const int HoldFlags = 1;
    public const int HoldDeathPenalty = 5;
    public const int RespawnSecs = 5;

    public const int ResetFlagTime = 10000;
    public const int HoldResetFlagTime = 30000;
    public const int InvisFlagTime = 20000;

    private readonly List<FlagState> _flags = new();
    private readonly List<Vector3> _holdSpawns = new();
    private readonly int[] _scores = new int[2]; // Index 0 = good (team 1), Index 1 = evil (team 2)

    // Reverse carrier index for the owner model stored in FlagState.Owner: player id -> flag id.
    private readonly Dictionary<int, int> _carriersByPlayer = new();

    public IReadOnlyList<FlagState> Flags => _flags;
    public IReadOnlyList<Vector3> HoldSpawns => _holdSpawns;

    /// <summary>Active game mode info governing flag rules.</summary>
    public GameModeInfo Mode { get; set; } = GameModeCatalog.Get("ctf");

    /// <summary>Invoked whenever an audio event occurs.</summary>
    public event Action<FlagAudioEvent>? OnAudioEvent;

    public int GetScore(int team) => team is >= 1 and <= 2 ? _scores[team - 1] : 0;

    public int SetScore(int team, int score)
    {
        if (team is >= 1 and <= 2)
            return _scores[team - 1] = score;
        return 0;
    }

    public int AddScore(int team, int delta)
    {
        if (team is >= 1 and <= 2)
            return _scores[team - 1] += delta;
        return 0;
    }

    /// <summary>
    /// Player id currently carrying <paramref name="flagId"/>, or <c>null</c> when the
    /// flag is at base/dropped or the id is invalid. O(1) read of the owner model.
    /// </summary>
    public int? GetCarrier(int flagId)
        => flagId >= 0 && flagId < _flags.Count ? _flags[flagId].Owner : null;

    /// <summary>
    /// Flag id currently carried by <paramref name="playerId"/>, or <c>null</c> when that
    /// player carries nothing. O(1) read of the reverse carrier index.
    /// </summary>
    public int? GetCarriedFlag(int playerId)
        => _carriersByPlayer.TryGetValue(playerId, out int flagId) ? flagId : null;

    /// <summary>Flag state currently carried by <paramref name="playerId"/>, or <c>null</c>.</summary>
    public FlagState? GetCarriedFlagState(int playerId)
        => GetCarriedFlag(playerId) is int flagId ? _flags[flagId] : null;

    /// <summary>
    /// Single mutation point for flag ownership: updates <see cref="FlagState.Owner"/> and the
    /// reverse carrier index together so the two can never disagree.
    /// </summary>
    private void SetCarrier(int flagId, int? playerId)
    {
        var f = _flags[flagId];
        if (f.Owner.HasValue && _carriersByPlayer.TryGetValue(f.Owner.Value, out int mapped) && mapped == flagId)
            _carriersByPlayer.Remove(f.Owner.Value);
        f.Owner = playerId;
        if (playerId.HasValue) _carriersByPlayer[playerId.Value] = flagId;
    }

    public void ResetFlags()
    {
        _holdSpawns.Clear();
        _flags.Clear();
        _carriersByPlayer.Clear();
        _scores[0] = 0;
        _scores[1] = 0;
    }

    public bool AddFlag(int id, Vector3 spawnLoc, int team, int lastMillis = 0, bool isProtect = false, float spawnAngle = 0f)
    {
        if (id < 0 || id >= MaxFlags) return false;
        while (_flags.Count <= id)
        {
            _flags.Add(new FlagState { Id = _flags.Count });
        }
        var f = _flags[id];
        SetCarrier(id, null);
        f.Reset();
        f.Id = id;
        f.Team = team;
        f.SpawnLoc = spawnLoc;
        f.SpawnAngle = spawnAngle;
        f.InvisTime = isProtect ? lastMillis : 0;
        return true;
    }

    public bool AddHoldSpawn(Vector3 pos)
    {
        if (_holdSpawns.Count >= MaxHoldSpawns) return false;
        _holdSpawns.Add(pos);
        return true;
    }

    public void SetupHoldSpawns(Random? rng = null)
    {
        if (_holdSpawns.Count == 0) return;
        while (_flags.Count < HoldFlags)
        {
            int i = _flags.Count;
            if (!AddFlag(i, Vector3.Zero, 0, 0, false)) break;
            SpawnHoldFlag(i, rng);
            _flags[i].Version++;
        }
    }

    public void SpawnHoldFlag(int flagId, Random? rng = null)
    {
        if (_holdSpawns.Count == 0 || flagId < 0 || flagId >= _flags.Count) return;
        var f = _flags[flagId];
        int spawnIndex = f.SpawnIndex;
        rng ??= Random.Shared;
        for (int j = 0; j < 4; j++)
        {
            spawnIndex = rng.Next(_holdSpawns.Count);
            if (spawnIndex != f.SpawnIndex) break;
        }
        f.SpawnIndex = spawnIndex;
        f.SpawnLoc = _holdSpawns[spawnIndex];
    }

    public static bool InsideBase(FlagState f, Vector3 o)
    {
        float dx = f.SpawnLoc.X - o.X;
        float dy = f.SpawnLoc.Y - o.Y;
        float dz = f.SpawnLoc.Z - o.Z;
        return dx * dx + dy * dy <= BaseRadius * BaseRadius && Math.Abs(dz) <= BaseHeight;
    }

    public void OwnFlag(int flagId, int ownerId, int nowMillis, int ownTeam = -1)
    {
        if (flagId < 0 || flagId >= _flags.Count) return;
        var f = _flags[flagId];
        SetCarrier(flagId, ownerId);
        f.OwnTime = nowMillis;

        if (f.HoldTime > 0 && f.Team == ownTeam)
        {
            if (f.DropTime > 0 && f.DropTime < nowMillis)
                f.HoldTime += 2 * (nowMillis - f.DropTime);
            f.HoldTime = Math.Min(f.HoldTime, nowMillis);
        }
        else
        {
            f.HoldTime = nowMillis;
            if (ownTeam >= 0) f.Team = ownTeam;
        }

        if (ownerId == f.Dropper)
        {
            if (f.DropCount < int.MaxValue) f.DropCount++;
        }
        else
        {
            f.DropCount = 0;
        }
        f.Dropper = -1;
        f.InvisTime = 0;
    }

    public void DropFlag(int flagId, Vector3 dropLoc, int nowMillis, int dropperId = -1, bool penalty = false, float dropAngle = 0f)
    {
        if (flagId < 0 || flagId >= _flags.Count) return;
        var f = _flags[flagId];
        f.DropLoc = dropLoc;
        f.DropTime = nowMillis;
        f.DropAngle = dropAngle;
        if (dropperId < 0) f.DropCount = 0;
        else if (penalty) f.DropCount = int.MaxValue;
        f.Dropper = dropperId;
        SetCarrier(flagId, null);
        f.InvisTime = 0;
    }

    public void ReturnFlag(int flagId, int invisTime = 0)
    {
        if (flagId < 0 || flagId >= _flags.Count) return;
        var f = _flags[flagId];
        f.DropTime = 0;
        f.HoldTime = 0;
        f.DropCount = 0;
        SetCarrier(flagId, null);
        f.Dropper = -1;
        f.InvisTime = invisTime;
    }

    public void DeadFlag(int flagId, int nowMillis)
    {
        if (flagId < 0 || flagId >= _flags.Count) return;
        var f = _flags[flagId];
        if (f.HoldTime > 0)
        {
            int limit = !f.HasOwner && f.DropTime > 0 && f.DropTime >= f.HoldTime ? f.DropTime : nowMillis;
            f.HoldTime += HoldDeathPenalty * 1000;
            if (f.HoldTime >= limit) f.HoldTime = 0;
        }
    }

    /// <summary>
    /// Processes a player attempting to take/touch a flag using the manager's active <see cref="Mode"/>.
    /// </summary>
    public bool TakeFlag(int playerId, int playerTeam, Vector3 playerPos, int flagId, int version, int nowMillis, Action<int, int>? onPlayerScore = null, Random? rng = null)
        => TakeFlag(playerId, playerTeam, playerPos, flagId, version, Mode, nowMillis, onPlayerScore, rng);

    /// <summary>
    /// Processes a player attempting to take/touch a flag.
    /// Handles CTF steals, CTF captures at base, CTF returns of dropped friendly flags,
    /// Protect pickups, Protect scoring touches, and Hold pickups.
    /// </summary>
    public bool TakeFlag(int playerId, int playerTeam, Vector3 playerPos, int flagId, int version, GameModeInfo mode, int nowMillis, Action<int, int>? onPlayerScore = null, Random? rng = null)
    {
        if (flagId < 0 || flagId >= _flags.Count || playerTeam is < 1 or > 2) return false;
        var f = _flags[flagId];

        bool isHold = mode.Has(ModeFlags.Hold);
        bool isProtect = mode.Has(ModeFlags.Protect);

        if ((isHold ? f.SpawnIndex < 0 : f.Team is < 1 or > 2) ||
            f.Owner.HasValue ||
            f.Version != version ||
            (f.DropTime > 0 && f.Dropper == playerId && f.DropCount >= 3))
        {
            return false;
        }

        if (isHold || (isProtect == (f.Team == playerTeam)))
        {
            // Cannot take if already carrying any flag
            if (GetCarriedFlag(playerId).HasValue) return false;

            OwnFlag(flagId, playerId, nowMillis, isHold ? playerTeam : -1);
            f.Version++;
            DispatchAudio(GameSound.FlagPickup, flagId, f.Team, playerId, playerPos);
            return true;
        }
        else if (isProtect)
        {
            // Protect: touch enemy flag to score and reset it
            if (f.InvisTime == 0 || nowMillis >= f.InvisTime + InvisFlagTime)
            {
                ScoreFlag(playerId, playerTeam, flagId, -1, mode, nowMillis, onPlayerScore, rng);
                return true;
            }
            return false;
        }
        else if (f.DropTime > 0)
        {
            // CTF: touch dropped friendly flag to return it (must be near dropped location, not base)
            if (playerPos.DistanceTo(f.DropLoc) > FlagRadius)
            {
                return false;
            }
            ReturnFlag(flagId);
            f.Version++;
            DispatchAudio(GameSound.FlagReturn, flagId, f.Team, playerId, playerPos);
            return true;
        }
        else
        {
            // CTF: friendly flag is home; check if player is carrying enemy flag to capture
            // and ensure friendly flag is not currently dropped
            if (f.DropTime > 0) return false;
            if (GetCarriedFlag(playerId) is int relayFlagId)
            {
                ScoreFlag(playerId, playerTeam, flagId, relayFlagId, mode, nowMillis, onPlayerScore, rng);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Scores a flag using the manager's active <see cref="Mode"/>.
    /// </summary>
    public void ScoreFlag(int playerId, int playerTeam, int goalFlagId, int relayFlagId, int nowMillis, Action<int, int>? onPlayerScore = null, Random? rng = null)
        => ScoreFlag(playerId, playerTeam, goalFlagId, relayFlagId, Mode, nowMillis, onPlayerScore, rng);

    /// <summary>
    /// Scores a flag: returns the relay flag (or goal flag in Protect/Hold), awards point to team and player,
    /// updates scores and dispatches audio event.
    /// </summary>
    public void ScoreFlag(int playerId, int playerTeam, int goalFlagId, int relayFlagId, GameModeInfo mode, int nowMillis, Action<int, int>? onPlayerScore = null, Random? rng = null)
    {
        bool isHold = mode.Has(ModeFlags.Hold);
        bool isProtect = mode.Has(ModeFlags.Protect);

        int flagToReturn = relayFlagId >= 0 ? relayFlagId : goalFlagId;
        ReturnFlag(flagToReturn, isProtect ? nowMillis : 0);

        int teamScore = AddScore(playerTeam, 1);
        onPlayerScore?.Invoke(playerId, 1);

        if (relayFlagId >= 0 && relayFlagId < _flags.Count)
        {
            _flags[relayFlagId].Version++;
        }
        if (goalFlagId >= 0 && goalFlagId < _flags.Count)
        {
            _flags[goalFlagId].Version++;
            if (isHold)
            {
                SpawnHoldFlag(goalFlagId, rng);
            }
        }

        Vector3 pos = goalFlagId >= 0 && goalFlagId < _flags.Count ? _flags[goalFlagId].SpawnLoc : Vector3.Zero;
        DispatchAudio(GameSound.FlagScore, goalFlagId, playerTeam, playerId, pos);
    }

    /// <summary>
    /// Drops any flag currently carried by the player using the manager's active <see cref="Mode"/>.
    /// </summary>
    public void DropPlayerFlag(int playerId, Vector3 playerPos, int nowMillis, int dropperId = -1, bool tkPenalty = false, float dropAngle = 0f)
        => DropPlayerFlag(playerId, playerPos, Mode, nowMillis, dropperId, tkPenalty, dropAngle);

    /// <summary>
    /// Drops any flag currently carried by the player.
    /// In Protect mode, if dropped inside its own base, it returns instead.
    /// </summary>
    public void DropPlayerFlag(int playerId, Vector3 playerPos, GameModeInfo mode, int nowMillis, int dropperId = -1, bool tkPenalty = false, float dropAngle = 0f)
    {
        if (GetCarriedFlag(playerId) is not int carriedFlagId) return;
        bool isProtect = mode.Has(ModeFlags.Protect);
        var f = _flags[carriedFlagId];
        if (isProtect && InsideBase(f, playerPos))
        {
            ReturnFlag(f.Id);
            f.Version++;
            DispatchAudio(GameSound.FlagReturn, f.Id, f.Team, playerId, playerPos);
        }
        else
        {
            f.Version++;
            DropFlag(f.Id, playerPos, nowMillis, dropperId >= 0 ? dropperId : playerId, tkPenalty, dropAngle);
            DispatchAudio(GameSound.FlagDrop, f.Id, f.Team, playerId, playerPos);
        }
    }

    /// <summary>
    /// Player died: handles hold mode death penalty, drops carried flags, and cleans up dropper references using active <see cref="Mode"/>.
    /// </summary>
    public void OnPlayerDied(int victimId, int? actorId, bool isTeammate, Vector3 victimPos, int nowMillis, float dropAngle = 0f)
        => OnPlayerDied(victimId, actorId, isTeammate, victimPos, Mode, nowMillis, dropAngle);

    /// <summary>
    /// Player died: handles hold mode death penalty, drops carried flags, and cleans up dropper references.
    /// </summary>
    public void OnPlayerDied(int victimId, int? actorId, bool isTeammate, Vector3 victimPos, GameModeInfo mode, int nowMillis, float dropAngle = 0f)
    {
        bool isHold = mode.Has(ModeFlags.Hold);
        if (isHold && GetCarriedFlag(victimId) is int heldFlagId)
        {
            DeadFlag(heldFlagId, nowMillis);
        }

        bool tkPenalty = actorId.HasValue && actorId.Value != victimId && isTeammate;
        int dropper = tkPenalty ? actorId!.Value : victimId;
        DropPlayerFlag(victimId, victimPos, mode, nowMillis, dropper, tkPenalty, dropAngle);

        foreach (var f in _flags)
        {
            if (f.Dropper == victimId)
            {
                f.Dropper = -1;
                f.DropCount = 0;
            }
        }
    }

    /// <summary>
    /// Regular tick update for flags using active <see cref="Mode"/>.
    /// </summary>
    public void Update(int nowMillis, Func<int, (int team, Vector3 pos)?>? getPlayerInfo = null, Action<int, int>? onPlayerScore = null, Random? rng = null)
        => Update(Mode, nowMillis, getPlayerInfo, onPlayerScore, rng);

    /// <summary>
    /// Regular tick update for flags: handles drop reset timeouts, invis timeouts, and hold 20s score countdown.
    /// </summary>
    public void Update(GameModeInfo mode, int nowMillis, Func<int, (int team, Vector3 pos)?>? getPlayerInfo = null, Action<int, int>? onPlayerScore = null, Random? rng = null)
    {
        bool isHold = mode.Has(ModeFlags.Hold);
        bool isProtect = mode.Has(ModeFlags.Protect);

        for (int i = 0; i < _flags.Count; i++)
        {
            var f = _flags[i];
            int timeout = isHold ? HoldResetFlagTime : ResetFlagTime;

            // Reset dropped flag after timeout
            if (!f.HasOwner && f.DropTime > 0 && nowMillis - f.DropTime >= timeout)
            {
                ReturnFlag(i, isProtect ? nowMillis : 0);
                if (isHold)
                {
                    SpawnHoldFlag(i, rng);
                }
                f.Version++;

                int penaltyTeam = isHold ? 0 : f.Team;
                if (!isHold && isProtect)
                {
                    AddScore(f.Team, -1);
                }

                DispatchAudio(GameSound.FlagReset, i, penaltyTeam, null, f.SpawnLoc);
            }

            // Clear invis time after INVISFLAGTIME (protect score cooldown)
            if (f.InvisTime > 0 && nowMillis - f.InvisTime >= InvisFlagTime)
            {
                f.InvisTime = 0;
            }

            // Hold mode 20s score check
            if (isHold && f.HasOwner && nowMillis - f.HoldTime >= HoldSecs * 1000)
            {
                int ownerId = f.Owner!.Value;
                var pinfo = getPlayerInfo?.Invoke(ownerId);
                if (pinfo.HasValue)
                {
                    ScoreFlag(ownerId, pinfo.Value.team, i, -1, mode, nowMillis, onPlayerScore, rng);
                }
                else
                {
                    SpawnHoldFlag(i, rng);
                    f.Version++;
                    ReturnFlag(i, 0);
                    DispatchAudio(GameSound.FlagReset, i, 0, null, f.SpawnLoc);
                }
            }
        }
    }

    private void DispatchAudio(GameSound sound, int flagId, int team, int? playerId, Vector3? pos)
    {
        OnAudioEvent?.Invoke(new FlagAudioEvent(sound, flagId, team, playerId, pos));
    }
}
