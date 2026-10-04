using Godot;

// Sauerbraten (Cube 2) movement MVP, ported from SVN r6889.
// Only the velocity model is ported here; see docs/movement-physics.md.
public partial class Player : CharacterBody3D
{
    private const float MaxSpeed = 100.0f; // physent::maxspeed        (src/shared/ents.h:91)
    private const float Gravity  = 200.0f; // src/engine/physics.cpp:478
    private const float JumpVel  = 125.0f; // src/engine/physics.cpp:477
    private const float Sens     = 0.0022f; // mouse look, tunable

    private Vector3 _vel = Vector3.Zero;     // physent::vel     (horizontal + jump impulse)
    private Vector3 _falling = Vector3.Zero; // physent::falling (gravity accumulation)
    private bool _jumping = false;           // physent::jumping (set on press, consumed on ground)
    private bool _jumpHeld = false;

    private Camera3D _cam = null!;

    public override void _Ready()
    {
        _cam = GetNode<Camera3D>("Camera3D");
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateY(-mm.Relative.X * Sens);
            Vector3 rot = _cam.Rotation;
            rot.X = Mathf.Clamp(rot.X - mm.Relative.Y * Sens, -1.5533f, 1.5533f);
            _cam.Rotation = rot;
        }
        else if (@event.IsActionPressed("ui_cancel"))
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        bool onFloor = IsOnFloor();

        // jumping follows the key state but is only set on a fresh press (original
        // sets it from the jump command's press/release events, physics.cpp:2019);
        // it is consumed when applied, so holding the key does not auto-hop.
        bool held = Input.IsActionPressed("jump");
        if (held && !_jumpHeld) _jumping = true;
        else if (!held) _jumping = false;
        _jumpHeld = held;

        // vecfromyawpitch (physics.cpp:1548): move/strafe -> wish direction
        float fwd = Input.GetAxis("move_back", "move_forward");
        float left = Input.GetAxis("strafe_right", "strafe_left");
        Vector3 wish = Vector3.Zero;
        if (fwd != 0.0f || left != 0.0f)
        {
            wish = GlobalTransform.Basis * new Vector3(-left, 0.0f, -fwd);
            if (onFloor) // modifyvelocity: follow the slope plane
            {
                Vector3 n = GetFloorNormal();
                wish.Y = -(wish.X * n.X + wish.Z * n.Z) / n.Y;
            }
            wish = wish.Normalized();
        }

        // modifyvelocity (physics.cpp:1589)
        Vector3 target = wish * MaxSpeed;
        if (fwd != 0.0f && left == 0.0f) target *= 1.3f; // forward-only bonus
        if (!onFloor) target *= 1.3f;                    // airborne bonus
        float fric = onFloor ? 6.0f : 30.0f;
        _vel = target + (_vel - target) * Mathf.Pow(1.0f - 1.0f / fric, dt / 0.02f);

        // modifygravity (physics.cpp:1650) + jump (physics.cpp:1603)
        if (onFloor)
        {
            _falling = Vector3.Zero;
            if (_jumping)
            {
                _jumping = false;
                _vel.Y = Mathf.Max(_vel.Y, JumpVel);
            }
        }
        else
        {
            _falling.Y -= Gravity * dt;
        }

        Velocity = _vel + _falling;
        MoveAndSlide();

        // keep vel/falling consistent with what the collision solver actually did
        Vector3 v = Velocity;
        _vel.X = v.X;
        _vel.Z = v.Z;
        if (IsOnFloor() && v.Y <= 0.0f)
        {
            _vel.Y = 0.0f;
            _falling = Vector3.Zero;
        }
    }
}
