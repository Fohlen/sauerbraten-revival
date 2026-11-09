using Godot;

namespace SauerbratenRevival.World;

/// <summary>
/// Sauerbraten coop-edit style free-fly camera: WASD moves along the camera's
/// full look direction (forward includes pitch, so looking down flies down), the
/// mouse looks/tilts, Esc releases the mouse and a click recaptures. Shift sprints.
/// </summary>
[GlobalClass]
public partial class FreeFlyCamera : Camera3D
{
    [Export] public float Speed { get; set; } = 400.0f;
    [Export] public float SprintMultiplier { get; set; } = 3.0f;
    [Export] public float Sensitivity { get; set; } = 0.0022f;

    /// <summary>When true (default), automatically becomes current and captures mouse on ready.</summary>
    [Export] public bool AutoActivate { get; set; } = true;

    private bool _captured;

    /// <summary>Whether mouse capture is active for this camera.</summary>
    public bool IsCaptured => _captured;

    public override void _Ready()
    {
        if (Engine.IsEditorHint()) return;
        if (AutoActivate)
        {
            Current = true;
            Capture(true);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Engine.IsEditorHint()) return;

        if (@event is InputEventMouseMotion motion && _captured)
        {
            ApplyMouse(motion.Relative);
        }
        else if (@event.IsActionPressed("ui_cancel"))
        {
            Capture(false);
        }
        else if (@event is InputEventMouseButton { Pressed: true } && !_captured)
        {
            Capture(true);
        }
    }

    public override void _Process(double delta)
    {
        if (Engine.IsEditorHint() || !_captured) return;

        float forward = Input.GetAxis("move_back", "move_forward");
        float strafe = Input.GetAxis("strafe_left", "strafe_right");
        bool sprint = Input.IsKeyPressed(Key.Shift);
        Step(forward, strafe, (float)delta, sprint);
    }

    /// <summary>Rotates camera yaw and clamps pitch from mouse motion delta.</summary>
    public void ApplyMouse(Vector2 relative)
    {
        RotateY(-relative.X * Sensitivity);
        Vector3 rot = Rotation;
        rot.X = Mathf.Clamp(rot.X - relative.Y * Sensitivity, -Mathf.Pi / 2f + 0.01f, Mathf.Pi / 2f - 0.01f);
        Rotation = rot;
    }

    /// <summary>Moves camera position along view basis.</summary>
    public void Step(float forward, float strafe, float delta, bool sprint = false)
    {
        Vector3 dir = GlobalTransform.Basis * new Vector3(strafe, 0.0f, -forward);
        if (dir.LengthSquared() < 1e-6f) return;
        dir = dir.Normalized();

        float speed = Speed * (sprint ? SprintMultiplier : 1.0f);
        GlobalPosition += dir * speed * delta;
    }

    public void Capture(bool on)
    {
        _captured = on;
        Input.MouseMode = on ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
    }
}
