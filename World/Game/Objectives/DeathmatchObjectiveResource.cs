using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game.Objectives;

[Tool]
[GlobalClass]
public partial class DeathmatchObjectiveResource : ModeObjectiveResource
{
    [Export] public int FragLimit { get; set; } = 0;

    public DeathmatchObjectiveResource()
    {
        Id = "dm";
        Name = "deathmatch";
        DefaultTeamPlay = false;
        DefaultMapPool = MapListId.Dm;
        DefaultSpawnArmour = 25;
    }

    public override IModeObjective CreateInstance(GameModeInfo mode) => new DeathmatchObjective(this, mode);
}

public class DeathmatchObjective : IModeObjective
{
    public ModeObjectiveResource Definition => Resource;
    public DeathmatchObjectiveResource Resource { get; }
    public GameModeInfo Mode { get; }

    private readonly Dictionary<int, int> _fragsByPlayer = new();
    private readonly int[] _teamScores = new int[2]; // 0 = team 1, 1 = team 2

    public DeathmatchObjective(DeathmatchObjectiveResource resource, GameModeInfo mode)
    {
        Resource = resource;
        Mode = mode;
    }

    public int GetPlayerFrags(int playerId) => _fragsByPlayer.TryGetValue(playerId, out int f) ? f : 0;
    public int GetTeamScore(int team) => team is >= 1 and <= 2 ? _teamScores[team - 1] : 0;

    public void OnPlayerDied(MapPlayer victim, MapPlayer? killer, Gun gun, int nowMillis)
    {
        if (killer == null || killer == victim)
        {
            // Suicide penalty
            _fragsByPlayer[victim.PlayerId] = GetPlayerFrags(victim.PlayerId) - 1;
            if (Mode.TeamPlay && victim.Team is >= 1 and <= 2)
                _teamScores[victim.Team - 1]--;
            return;
        }

        if (Mode.TeamPlay && killer.Team == victim.Team)
        {
            // Teamkill penalty
            _fragsByPlayer[killer.PlayerId] = GetPlayerFrags(killer.PlayerId) - 1;
            if (killer.Team is >= 1 and <= 2)
                _teamScores[killer.Team - 1]--;
            return;
        }

        // Frag
        _fragsByPlayer[killer.PlayerId] = GetPlayerFrags(killer.PlayerId) + 1;
        if (Mode.TeamPlay && killer.Team is >= 1 and <= 2)
            _teamScores[killer.Team - 1]++;
    }

    public ObjectiveHudState GetHudState(MapPlayer localPlayer, int nowMillis)
    {
        if (Mode.TeamPlay)
        {
            int myTeam = localPlayer.Team;
            int enemyTeam = myTeam == 1 ? 2 : 1;
            int myScore = GetTeamScore(myTeam);
            int enemyScore = GetTeamScore(enemyTeam);
            return new ObjectiveHudState(
                PrimaryScore: myScore,
                SecondaryScore: enemyScore,
                PrimaryIsFriendly: true);
        }

        return new ObjectiveHudState(
            PrimaryScore: GetPlayerFrags(localPlayer.PlayerId),
            PrimaryIsFriendly: true);
    }
}
