using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Runtime;

namespace SauerbratenRevival.World.Game.Objectives;

[Tool]
[GlobalClass]
public partial class RaceObjectiveResource : ModeObjectiveResource
{
    [Export] public int Laps { get; set; } = 1;
    [Export] public bool RespawnAtCheckpoint { get; set; } = true;
    [Export] public float CheckpointRadius { get; set; } = 20.0f;
    [Export] public Script? CourseEventScript { get; set; }

    public RaceObjectiveResource()
    {
        Id = "race";
        Name = "race";
        DefaultTeamPlay = false;
        DefaultMapPool = MapListId.Dm;
        DefaultSpawnArmour = 0;
    }

    public override IModeObjective CreateInstance(GameModeInfo mode) => new RaceObjective(this, mode);
}

public class RaceObjective : IModeObjective
{
    public ModeObjectiveResource Definition => Resource;
    public RaceObjectiveResource Resource { get; }
    public GameModeInfo Mode { get; }

    public record Checkpoint(int Index, Vector3 Position, float Yaw);

    private readonly List<Checkpoint> _checkpoints = new();
    private readonly Dictionary<int, (int CheckpointIdx, int Lap)> _progressByPlayer = new();

    public IReadOnlyList<Checkpoint> Checkpoints => _checkpoints;

    public RaceObjective(RaceObjectiveResource resource, GameModeInfo mode)
    {
        Resource = resource;
        Mode = mode;
    }

    public bool ClaimsEntity(MapEntity e) =>
        e.Type is EntityType.RespawnPoint or EntityType.Carrot;

    public void SetupWorld(IGameWorld world, IReadOnlyList<MapEntity> entities, Node3D objectiveRoot)
    {
        _checkpoints.Clear();
        _progressByPlayer.Clear();

        int idx = 0;
        foreach (var e in entities)
        {
            if (e.Type is EntityType.RespawnPoint or EntityType.Carrot)
            {
                // attr1 = yaw, attr2 = checkpoint index (optional)
                int order = e.Attr2 > 0 ? e.Attr2 : idx;
                _checkpoints.Add(new Checkpoint(order, e.Position, e.Attr1));
                idx++;
            }
        }
        _checkpoints.Sort((a, b) => a.Index.CompareTo(b.Index));
    }

    public void StepPlayer(MapPlayer player, int nowMillis)
    {
        if (_checkpoints.Count == 0 || !player.IsAlive) return;

        if (!_progressByPlayer.TryGetValue(player.PlayerId, out var prog))
            prog = (0, 0);

        int targetIdx = prog.CheckpointIdx % _checkpoints.Count;
        var targetCp = _checkpoints[targetIdx];

        if (player.Entity.FeetPos().DistanceTo(targetCp.Position) <= Resource.CheckpointRadius)
        {
            int nextIdx = prog.CheckpointIdx + 1;
            int nextLap = nextIdx / _checkpoints.Count;
            _progressByPlayer[player.PlayerId] = (nextIdx, nextLap);
        }
    }

    public (Vector3 FeetPos, float Yaw)? ResolveSpawn(MapPlayer player)
    {
        if (!Resource.RespawnAtCheckpoint || _checkpoints.Count == 0) return null;

        if (_progressByPlayer.TryGetValue(player.PlayerId, out var prog) && prog.CheckpointIdx > 0)
        {
            int lastIdx = (prog.CheckpointIdx - 1) % _checkpoints.Count;
            var cp = _checkpoints[lastIdx];
            return (cp.Position, cp.Yaw);
        }

        return null;
    }

    public ObjectiveHudState GetHudState(MapPlayer localPlayer, int nowMillis)
    {
        if (_progressByPlayer.TryGetValue(localPlayer.PlayerId, out var prog))
        {
            int currentCp = prog.CheckpointIdx % (_checkpoints.Count > 0 ? _checkpoints.Count : 1);
            return new ObjectiveHudState(
                PrimaryScore: currentCp + 1,
                SecondaryScore: prog.Lap + 1,
                PrimaryIsFriendly: true);
        }

        return new ObjectiveHudState(
            PrimaryScore: 1,
            SecondaryScore: 1,
            PrimaryIsFriendly: true);
    }
}
