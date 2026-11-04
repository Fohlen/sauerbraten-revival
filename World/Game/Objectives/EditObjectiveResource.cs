using Godot;

namespace SauerbratenRevival.World.Game.Objectives;

[Tool]
[GlobalClass]
public partial class EditObjectiveResource : ModeObjectiveResource
{
    public EditObjectiveResource()
    {
        Id = "edit";
        Name = "edit";
        DefaultTeamPlay = false;
        IsEditMode = true;
        DefaultMapPool = MapListId.Dm;
        DefaultSpawnArmour = 25;
    }

    public override IModeObjective CreateInstance(GameModeInfo mode) => new EditObjective(this, mode);
}

public class EditObjective : IModeObjective
{
    public ModeObjectiveResource Definition => Resource;
    public EditObjectiveResource Resource { get; }
    public GameModeInfo Mode { get; }

    public EditObjective(EditObjectiveResource resource, GameModeInfo mode)
    {
        Resource = resource;
        Mode = mode;
    }
}
