using Godot;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;
using HudgunModel = SauerbratenRevival.World.Models.HudgunModel;
using PlayerModel = SauerbratenRevival.World.Models.PlayerModel;

namespace SauerbratenRevival.World.Game.Players;

/// <summary>
/// Presentation controller for <see cref="MapPlayer"/>. Manages node transforms,
/// first-person vs third-person camera offsets, FOV conversions, and model/hudgun
/// visibility and animations.
///
/// Separating presentation from physics ensures discrete 5 ms simulation steps
/// do not churn camera transforms or animation state. Presentation nodes update
/// during <see cref="_Process"/> by observing the player's simulated state.
/// </summary>
public partial class PlayerView : Node3D
{
    /// <summary>The player whose simulated state is presented by this view.</summary>
    public MapPlayer? Player { get; set; }

    /// <summary>The active scene camera for this player.</summary>
    public Camera3D? Camera { get; set; }

    /// <summary>The placeholder body capsule mesh instance, if present.</summary>
    public MeshInstance3D? PlaceholderBody { get; set; }

    /// <summary>The visible third-person character body model, if attached.</summary>
    public PlayerModel? PlayerModel { get; set; }

    /// <summary>The visible first-person hands/weapon model, if attached.</summary>
    public HudgunModel? Hudgun { get; set; }

    /// <summary>When true, the placeholder capsule is never shown.</summary>
    public bool HidePlaceholderBody { get; set; }

    /// <summary>Original horizontal FOV (VARP fov, default 100).</summary>
    public float Fov { get; set; } = 100.0f;

    /// <summary>Third-person camera distance behind the eye (engine thirdpersondistance).</summary>
    public float ThirdPersonDistance { get; set; } = 0.0f;

    /// <summary>Third-person vertical offset (engine thirdpersonup).</summary>
    public float ThirdPersonUp { get; set; } = 0.0f;

    /// <summary>True while the camera is detached in third-person mode.</summary>
    public bool ThirdPerson { get; private set; }

    private int _lastActionSeen = -1;
    private int _lastPainSeen = -1;

    /// <summary>Resets cached animation action and pain timestamps.</summary>
    public void ResetAnimState()
    {
        _lastActionSeen = -1;
        _lastPainSeen = -1;
    }

    public PlayerView() { }

    public PlayerView(MapPlayer player)
    {
        Player = player;
    }

    public override void _Ready()
    {
        ResolveNodes();
        if (Camera != null)
        {
            Camera.Current = true;
            ApplyFov(Camera);
        }
        SyncAll();
    }

    /// <summary>Resolves child camera and placeholder body if not explicitly assigned.</summary>
    public void ResolveNodes()
    {
        var parentPlayer = Player ?? GetParent() as MapPlayer;
        if (parentPlayer != null)
        {
            Player = parentPlayer;
            Camera ??= parentPlayer.GetNodeOrNull<Camera3D>("Camera3D");
            PlaceholderBody ??= parentPlayer.GetNodeOrNull<MeshInstance3D>("Body");
            PlayerModel ??= parentPlayer.PlayerModel;
            Hudgun ??= parentPlayer.Hudgun;
        }
        else
        {
            Camera ??= GetNodeOrNull<Camera3D>("Camera3D");
            PlaceholderBody ??= GetNodeOrNull<MeshInstance3D>("Body");
        }
    }

    /// <summary>Toggles detached third-person camera mode.</summary>
    public void ToggleThirdPerson()
    {
        ThirdPerson = !ThirdPerson;
        SyncCamera();
    }

    /// <summary>Applies persisted user look settings and updates camera FOV.</summary>
    public void ApplyLookSettings(UserSettings settings)
    {
        Fov = settings.Fov;
        if (Camera != null) ApplyFov(Camera);
    }

    /// <summary>Converts horizontal FOV to Godot vertical FOV and applies it to the camera.</summary>
    public void ApplyFov(Camera3D camera)
    {
        float aspect = (float)camera.GetViewport().GetVisibleRect().Size.X
                     / (float)camera.GetViewport().GetVisibleRect().Size.Y;
        camera.Fov = MapPlayer.FovToVertical(Fov, aspect);
    }

    /// <summary>
    /// Synchronizes player node transform, camera position, body model, and hudgun.
    /// Safe to call immediately from tests and setup methods.
    /// </summary>
    public void SyncAll()
    {
        SyncTransform();
        SyncCamera();
        SyncBodyModel();
    }

    /// <summary>Updates the player's 3D position and orientation from physics.</summary>
    public void SyncTransform()
    {
        if (Player == null) return;
        Player.Position = Player.Entity.O;

        Vector3 forward = Movement.VecFromYawPitch(Player.Entity.Yaw, Player.Pitch, 1, 0);
        if (forward.LengthSquared() > 1e-6f)
        {
            Vector3 up = new Vector3(0, 0, 1);
            if (Mathf.Abs(forward.Dot(up)) > 0.999f)
            {
                float rad = Mathf.DegToRad(Player.Entity.Yaw);
                up = new Vector3(-Mathf.Sin(rad), Mathf.Cos(rad), 0);
            }
            Player.Basis = Basis.LookingAt(forward, up);
        }

        SyncCamera();
        SyncBodyModel();
    }

    /// <summary>
    /// Positions the Camera3D for first-person vs third-person view and updates
    /// visibility of the placeholder body, real player model, and hudgun.
    /// </summary>
    public void SyncCamera()
    {
        ResolveNodes();
        if (Camera == null) return;

        if (PlaceholderBody != null)
            PlaceholderBody.Visible = !HidePlaceholderBody && ThirdPerson;

        if (PlayerModel != null)
            PlayerModel.Visible = ThirdPerson;

        if (Hudgun != null)
            Hudgun.Visible = !ThirdPerson && !(Player?.Floating ?? false);

        if (!ThirdPerson)
        {
            Camera.Position = Vector3.Zero;
            return;
        }

        Camera.Position = new Vector3(0, ThirdPersonUp, ThirdPersonDistance);
        Camera.Basis = Basis.Identity;
    }

    /// <summary>
    /// Places the PlayerModel at the player's feet upright and facing yaw + 180°.
    /// </summary>
    public void SyncBodyModel()
    {
        ResolveNodes();
        if (Player == null || PlayerModel == null || !PlayerModel.IsInsideTree()) return;

        PlayerModel.Position = Player.Entity.O with { Z = Player.Entity.O.Z - MapPlayer.EyeHeight };
        Vector3 facing = Movement.VecFromYawPitch(Player.Entity.Yaw + 180f, 0f, 1, 0);
        if (facing.LengthSquared() > 1e-6f)
        {
            PlayerModel.Basis = Basis.LookingAt(facing, new Vector3(0, 0, 1));
        }
    }

    /// <summary>Immediately positions the body model and synchronizes the camera.</summary>
    public void SyncBodyModelNow()
    {
        SyncCamera();
        SyncBodyModel();
        UpdateBodyAnim();
    }

    /// <summary>Updates body model animation based on current player state.</summary>
    public void UpdateBodyAnim()
    {
        ResolveNodes();
        if (Player == null || PlayerModel == null) return;

        string name = Player.SelectBodyAnim();
        bool isNewAction = (Player.Combat.LastAction != _lastActionSeen && name.StartsWith("attack "))
                        || (Player.Combat.LastPain != _lastPainSeen && name == "pain");

        if (name != PlayerModel.CurrentAnim || isNewAction)
        {
            PlayerModel.Play(name, loop: MapPlayer.IsLoopingAnim(name), restart: isNewAction);
            _lastActionSeen = Player.Combat.LastAction;
            _lastPainSeen = Player.Combat.LastPain;
        }
    }

    /// <summary>Updates first-person hudgun animation.</summary>
    public void UpdateHudgunAnim()
    {
        ResolveNodes();
        if (Player == null || Hudgun == null) return;

        int attackDelay = WeaponCatalog.Get((Gun)Player.Combat.GunSelect).AttackDelay;
        Hudgun.UpdateAnim(Player.LastMillis, Player.Combat.LastAction, Player.Combat.LastAttackGun, Player.Combat.GunSelect, attackDelay);
    }

    /// <summary>Observes player state and refreshes presentation nodes during _Process.</summary>
    public override void _Process(double delta)
    {
        if (Player == null) return;
        SyncTransform();
        UpdateBodyAnim();
        UpdateHudgunAnim();
    }
}
