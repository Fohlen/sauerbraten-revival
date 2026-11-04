using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game.Players;

namespace SauerbratenRevival.World.Game.Objectives;

[Tool]
[GlobalClass]
public partial class CaptureObjectiveResource : ModeObjectiveResource
{
    public CaptureObjectiveResource()
    {
        Id = "capture";
        Name = "capture";
        DefaultTeamPlay = true;
        DefaultMapPool = MapListId.Capture;
        DefaultSpawnArmour = 100;
    }

    public override IModeObjective CreateInstance(GameModeInfo mode) => new CaptureObjective(this, mode);
}

public class CaptureObjective : IModeObjective
{
    public ModeObjectiveResource Definition => Resource;
    public CaptureObjectiveResource Resource { get; }
    public GameModeInfo Mode { get; }

    private readonly int[] _teamScores = new int[2];

    public CaptureObjective(CaptureObjectiveResource resource, GameModeInfo mode)
    {
        Resource = resource;
        Mode = mode;
    }

    // Bases are rendered by ModeEntityBuilder for CaptureObjective; we do not omit them.
    public bool ClaimsEntity(MapEntity e) => false;

    public int GetTeamScore(int team) => team is >= 1 and <= 2 ? _teamScores[team - 1] : 0;
    public void SetTeamScore(int team, int score)
    {
        if (team is >= 1 and <= 2) _teamScores[team - 1] = score;
    }

    public ObjectiveHudState GetHudState(MapPlayer localPlayer, int nowMillis)
    {
        int myTeam = localPlayer.Team;
        int enemyTeam = myTeam == 1 ? 2 : 1;
        return new ObjectiveHudState(
            PrimaryScore: GetTeamScore(myTeam),
            SecondaryScore: GetTeamScore(enemyTeam),
            PrimaryIsFriendly: true);
    }
}
