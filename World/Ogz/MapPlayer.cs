using Godot;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Ogz;

/// <summary>
/// The local player, driven by the faithfully-ported octree physics
/// (<see cref="World.Physics.OctaPhysics"/>) instead of Godot collision.
///
/// It lives **under the Z-up world node** (like <see cref="MapWorld"/>), so it
/// simulates in Sauerbraten coordinates and the parent's −90° rotation handles
/// the Y-up view. All positions/velocities are Sauerbraten units.
///
/// Input/timing replicate the original's captured behaviour
/// (see <c>docs/movement-verification.md</c>, layer 3–4):
/// <list type="bullet">
/// <item>the whole frame's mouse delta is applied <b>once per rendered frame</b>
///   (<see cref="_UnhandledInput"/>), never per physics tick;</item>
/// <item>physics advances in <b>fixed 5 ms</b> steps (<see cref="Phys.PhysFrameTime"/>)
///   caught up by <b>count</b> — a variable frame time never changes the step size.</item>
/// </list>
/// </summary>
[GlobalClass]
public partial class MapPlayer : Node3D
{
    /// <summary>Original <c>sensitivity</c> default (rendergl.cpp FVARP).</summary>
    [Export] public float Sensitivity { get; set; } = 3.0f;

    /// <summary>Original <c>sensitivityscale</c> default (rendergl.cpp FVARP).</summary>
    [Export] public float SensitivityScale { get; set; } = 1.0f;

    /// <summary>Original <c>mouseaccel</c> default (0 = disabled).</summary>
    [Export] public float MouseAccel { get; set; } = 0.0f;

    /// <summary>Spawn eye offset above the spawn point's feet (ents.h <c>eyeheight</c>).</summary>
    public const float EyeHeight = 14.0f;

    /// <summary>The simulated entity (Sauerbraten Z-up). Read by tests/diagnostics.</summary>
    public PhysEnt Entity { get; } = new();

    /// <summary>View pitch in degrees (the physics model only uses yaw; pitch is camera-only).</summary>
    public float Pitch { get; private set; }

    private OctaPhysics? _phys;
    private Vector2 _mouseDelta; // pending frame delta (screen px)

    // Fixed-step accumulator (seconds of un-simulated time).
    private double _accum;
    private const double StepSeconds = Phys.PhysFrameTime / 1000.0;

    /// <summary>True once <see cref="Setup"/> has run.</summary>
    public bool Initialized { get; private set; }

    public override void _Ready()
    {
        // Make this player's nested camera authoritative.
        if (GetNodeOrNull<Camera3D>("Camera3D") is { } cam) cam.Current = true;
    }

    /// <summary>
    /// Initialise the player against a built world. <paramref name="spawnFeet"/> is
    /// the spawn point position (Sauerbraten units, at the feet); the eye is raised
    /// by <see cref="EyeHeight"/> as the engine does (physics.cpp <c>entinmap</c>).
    /// </summary>
    public void Setup(Octa octa, Vector3 spawnFeet, float yawDegrees)
    {
        _phys = new OctaPhysics(octa);
        Entity.Type = Phys.EntPlayer;
        Entity.O = spawnFeet with { Z = spawnFeet.Z + EyeHeight };
        Entity.Yaw = yawDegrees;
        Pitch = 0f;
        Initialized = true;
        SyncTransform();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
            _mouseDelta += motion.Relative;
        else if (@event.IsActionPressed("ui_cancel"))
            Input.MouseMode = Input.MouseModeEnum.Visible;
        else if (@event is InputEventMouseButton { Pressed: true } && Input.MouseMode != Input.MouseModeEnum.Captured)
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// <summary>
    /// Apply a mouse delta (screen pixels: right = +X, down = +Y) to the view.
    /// Split out of <see cref="_UnhandledInput"/> so the mapping is unit-testable.
    /// Mirrors rendergl.cpp <c>mousemove</c>.
    /// </summary>
    public void ApplyMouseDelta(Vector2 delta)
    {
        _mouseDelta += delta;
        ApplyMouse();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Initialized || _phys == null) return;

        // Mouse: the whole frame's accumulated delta is applied once, before stepping
        // (rendergl.cpp mousemove). Every step this frame then sees the same yaw/pitch.
        ApplyMouse();

        // Movement keys (physics.cpp dir()): forward/back -> move, left/right -> strafe.
        // NOTE: strafe is negated relative to dir() because the same Godot/macOS
        // screen-X handedness that flips mouse yaw (see ApplyMouse) also flips the
        // engine's strafe sign, so A/D would otherwise be swapped.
        Entity.Move = (Input.IsActionPressed("move_forward") ? 1 : 0) - (Input.IsActionPressed("move_back") ? 1 : 0);
        Entity.Strafe = (Input.IsActionPressed("strafe_right") ? 1 : 0) - (Input.IsActionPressed("strafe_left") ? 1 : 0);
        Entity.Jumping = Input.IsActionPressed("jump");

        Advance(delta);
    }

    /// <summary>
    /// Advance the simulation by <paramref name="delta"/> seconds of real time,
    /// running whole fixed 5 ms steps caught up by count (a partial step is kept
    /// in the accumulator). Input is taken from <see cref="Entity"/> — set it first.
    /// </summary>
    public void Advance(double delta)
    {
        if (!Initialized || _phys == null) return;
        _accum += delta;
        int guard = 0;
        while (_accum >= StepSeconds && guard++ < 100)
        {
            _phys.MovePlayer(Entity, Phys.MoveRes, Phys.PhysFrameTime);
            _accum -= StepSeconds;
        }
        SyncTransform();
    }

    /// <summary>Set the view angles directly (tests/diagnostics).</summary>
    public void SetView(float yawDegrees, float pitchDegrees)
    {
        Entity.Yaw = yawDegrees;
        Pitch = pitchDegrees;
        SyncTransform();
    }

    /// <summary>rendergl.cpp <c>mousemove</c>: <c>yaw += dx*cursens</c>, <c>pitch -= dy*cursens</c>.</summary>
    private void ApplyMouse()
    {
        if (_mouseDelta == Vector2.Zero) return;
        Vector2 d = _mouseDelta;
        _mouseDelta.X = 0f;
        _mouseDelta.Y = 0f;

        // rendergl.cpp: cursens = sensitivity (+ accel*|delta|/curtime) / (33*sensitivityscale).
        // curtime is the frame time; approximate with the fixed step (accel is 0 by default).
        float cursens = Sensitivity;
        if (MouseAccel != 0f) cursens += MouseAccel * d.Length() / Phys.PhysFrameTime;
        cursens /= 33.0f * SensitivityScale;

        // rendergl.cpp mousemove: yaw += dx*cursens; pitch -= dy*cursens.
        // NOTE: the yaw sign is negated relative to the original because Godot's
        // captured-mouse Relative.X runs opposite to SDL's xrel on macOS, while
        // Relative.Y matches SDL's (down-positive) yrel. Hence yaw uses -d.X and
        // pitch keeps the original -d.Y.
        Entity.Yaw -= d.X * cursens;
        Pitch -= d.Y * cursens;
        FixCameraRange();
        SyncTransform();
    }

    /// <summary>rendergl.cpp <c>fixcamerarange</c>: wrap yaw 0..360, clamp pitch to ±90.</summary>
    private void FixCameraRange()
    {
        Pitch = Mathf.Clamp(Pitch, -90f, 90f);
        while (Entity.Yaw < 0f) Entity.Yaw += 360f;
        while (Entity.Yaw >= 360f) Entity.Yaw -= 360f;
    }

    /// <summary>Copy the simulated entity into the node transform (Sauerbraten Z-up).</summary>
    private void SyncTransform()
    {
        Position = Entity.O;
        Vector3 forward = Movement.VecFromYawPitch(Entity.Yaw, Pitch, 1, 0);
        if (forward.LengthSquared() > 1e-6f)
            Basis = Basis.LookingAt(forward, new Vector3(0, 0, 1));
    }
}
