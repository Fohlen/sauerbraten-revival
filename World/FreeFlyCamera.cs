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

    private bool _captured;

    public override void _Ready()
    {
        if (Engine.IsEditorHint()) return;
        Current = true;
        Capture(true);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Engine.IsEditorHint()) return;

        if (@event is InputEventMouseMotion motion && _captured)
        {
            RotateY(-motion.Relative.X * Sensitivity);
            Vector3 rot = Rotation;
            rot.X = Mathf.Clamp(rot.X - motion.Relative.Y * Sensitivity, -Mathf.Pi / 2f + 0.01f, Mathf.Pi / 2f - 0.01f);
            Rotation = rot;
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
        // Godot cameras look down -Z; the basis carries the pitch, so W flies
        // wherever you look (down when looking down).
        Vector3 dir = GlobalTransform.Basis * new Vector3(strafe, 0.0f, -forward);
        if (dir.LengthSquared() < 1e-6f) return;
        dir = dir.Normalized();

        float speed = Speed * (Input.IsKeyPressed(Key.Shift) ? SprintMultiplier : 1.0f);
        GlobalPosition += dir * speed * (float)delta;
    }

    private void Capture(bool on)
    {
        _captured = on;
        Input.MouseMode = on ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
    }
}
