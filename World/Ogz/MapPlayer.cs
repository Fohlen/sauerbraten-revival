using Godot;
using SauerbratenRevival.World.Entities;
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
    /// <summary>
    /// Original <c>fov</c> (rendergl.cpp VARP, default 100). This is the
    /// <b>horizontal</b> field of view; it is converted to Godot's vertical FOV via
    /// the engine's <c>fovy = 2*atan(tan(fov/2)/aspect)</c> (rendergl.cpp:1872).
    /// </summary>
    [Export] public float Fov { get; set; } = 100.0f;

    /// <summary>Original <c>sensitivity</c> default (rendergl.cpp FVARP).</summary>
    [Export] public float Sensitivity { get; set; } = 3.0f;

    /// <summary>Original <c>sensitivityscale</c> default (rendergl.cpp FVARP).</summary>
    [Export] public float SensitivityScale { get; set; } = 1.0f;

    /// <summary>Original <c>mouseaccel</c> default (0 = disabled).</summary>
    [Export] public float MouseAccel { get; set; } = 0.0f;

    /// <summary>Spawn eye offset above the spawn point's feet (ents.h <c>eyeheight</c>).</summary>
    public const float EyeHeight = 14.0f;

    /// <summary>
    /// Third-person camera distance behind the eye (engine <c>thirdpersondistance</c>, rendergl.cpp).
    /// <c>0</c> disables the offset.
    /// </summary>
    [Export] public float ThirdPersonDistance { get; set; } = 0.0f;

    /// <summary>
    /// Third-person vertical offset (engine <c>thirdpersonup</c>, rendergl.cpp). Negative
    /// looks down at the player; <c>0</c> keeps the eye height while pulling the camera in.
    /// </summary>
    [Export] public float ThirdPersonUp { get; set; } = 0.0f;

    /// <summary>True while the camera is detached from the player's eye (engine <c>isthirdperson()</c>).</summary>
    public bool ThirdPerson { get; private set; }

    /// <summary>
    /// When true, the placeholder <c>Body</c> capsule is never shown — the scene
    /// supplies a real <see cref="PlayerModel"/> instead (set by the viewer when it
    /// loads a converted body model).
    /// </summary>
    public bool HidePlaceholderBody { get; set; }

    /// <summary>
    /// The visible body model, if any. It must be a <b>sibling</b> of this node (a child
    /// of the same Z-up world node), so that it can be placed at the feet and kept
    /// upright independently of the camera's view pitch.
    /// </summary>
    public PlayerModel? PlayerModel { get; set; }

    /// <summary>Toggle the detached third-person camera (engine <c>thirdperson</c> bind).</summary>
    public void ToggleThirdPerson()
    {
        ThirdPerson = !ThirdPerson;
        SyncCamera();
    }

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
        // Make this player's nested camera authoritative and match the original FOV.
        if (GetNodeOrNull<Camera3D>("Camera3D") is { } cam)
        {
            cam.Current = true;
            ApplyFov(cam);
        }
    }

    /// <summary>
    /// Port the original's horizontal <see cref="Fov"/> onto the Godot camera as a
    /// vertical FOV (Godot <c>Fov</c> is vertical under the default KeepHeight mode).
    /// </summary>
    public void ApplyFov(Camera3D camera)
    {
        float aspect = (float)camera.GetViewport().GetVisibleRect().Size.X
                     / (float)camera.GetViewport().GetVisibleRect().Size.Y;
        camera.Fov = FovToVertical(Fov, aspect);
    }

    /// <summary>rendergl.cpp:1872 <c>fovy = 2*atan2(tan(fov/2), aspect)</c>.</summary>
    public static float FovToVertical(float horizontalFovDegrees, float aspect)
    {
        if (aspect <= 0f) return horizontalFovDegrees;
        float halfH = Mathf.DegToRad(horizontalFovDegrees) * 0.5f;
        return Mathf.RadToDeg(2f * Mathf.Atan(Mathf.Tan(halfH) / aspect));
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
        if (GetNodeOrNull<Camera3D>("Camera3D") is { } cam) ApplyFov(cam);
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

        // Toggle spectator/edit float (physics.cpp:5682 — CS_EDITING/CS_SPECTATOR
        // make moveplayer() fly). pitch feeds movement while floating (see
        // Movement.ModifyVelocity).
        if (Input.IsActionJustPressed("spectate")) ToggleFloat();

        // Toggle the detached third-person camera (engine `thirdperson`, rendergl.cpp).
        if (Input.IsActionJustPressed("third_person")) ToggleThirdPerson();

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
        Entity.Pitch = Pitch; // pitch feeds movement only while floating (vecfromyawpitch)
        _accum += delta;
        int guard = 0;
        while (_accum >= StepSeconds && guard++ < 100)
        {
            _phys.MovePlayer(Entity, Phys.MoveRes, Phys.PhysFrameTime);
            _accum -= StepSeconds;
        }
        SyncTransform();
    }

    /// <summary>
    /// Toggle the spectator/edit floating state (<c>CS_SPECTATOR</c>). While floating
    /// the player ignores gravity and collision and flies along the view direction,
    /// exactly like the engine's <c>moveplayer()</c> floating branch. Bound to
    /// <c>spectate</c> (F1).
    /// </summary>
    public void ToggleFloat()
    {
        Entity.State = Entity.State == PhysEnt.CsSpectator ? PhysEnt.CsAlive : PhysEnt.CsSpectator;
        if (Entity.State == PhysEnt.CsSpectator) Entity.Jumping = false;
    }

    /// <summary>True while the player is in the spectator/edit floating state.</summary>
    public bool Floating => Entity.State == PhysEnt.CsEditing || Entity.State == PhysEnt.CsSpectator;

    /// <summary>
    /// Place the nested <c>Camera3D</c> for the current view mode. In first person it sits
    /// at the eye (the node origin); in third person it is offset <b>backwards</b> along the
    /// view direction and <b>up</b> from the eye, matching the engine's detached camera
    /// (rendergl.cpp <c>recomputecamera</c>: <c>camera1->o += -dir*dist + up*up</c>). This is
    /// a pure camera offset — the player node stays at the eye and physics is untouched.
    /// </summary>
    private void SyncCamera()
    {
        if (GetNodeOrNull<Camera3D>("Camera3D") is not { } cam) return;

        // The placeholder capsule is shown only in third person and only when no real
        // PlayerModel is attached (`HidePlaceholderBody`); otherwise it is hidden
        // entirely so it never renders for a frame on startup.
        if (GetNodeOrNull<MeshInstance3D>("Body") is { } body) body.Visible = !HidePlaceholderBody && ThirdPerson;

        // The engine only renders the local player's body when the camera is detached
        // (`isthirdperson()`, render.cpp:343), so hide the real model in first person —
        // otherwise the camera sits inside it.
        if (PlayerModel is { } model) model.Visible = ThirdPerson;

        if (!ThirdPerson)
        {
            cam.Position = Vector3.Zero;
            return;
        }

        // The camera is a child with an identity local rotation, so it shares the
        // player's view orientation: its local -Z is forward (Godot cameras look down
        // -Z) and its local +Y is up. Pull back along local +Z and raise along local +Y.
        // (The player's local frame is Z-up in Sauerbraten terms: local +Y is the
        // world "up" axis.)
        cam.Position = new Vector3(0, ThirdPersonUp, ThirdPersonDistance);
        cam.Basis = Basis.Identity;
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

    /// <summary>(Re)place the body model now (e.g. right after attaching one).</summary>
    /// <summary>
    /// (Re)place the body model now, and apply the current view mode (e.g. hide it in
    /// first person) immediately — called right after a body is attached so it never
    /// renders for a frame before <see cref="SyncCamera"/> first runs.
    /// </summary>
    public void SyncBodyModelNow()
    {
        SyncCamera();
        SyncBodyModel();
    }

    /// <summary>Copy the simulated entity into the node transform (Sauerbraten Z-up).</summary>
    private void SyncTransform()
    {
        Position = Entity.O;
        Vector3 forward = Movement.VecFromYawPitch(Entity.Yaw, Pitch, 1, 0);
        if (forward.LengthSquared() > 1e-6f)
            Basis = Basis.LookingAt(forward, new Vector3(0, 0, 1));
        SyncCamera();
        SyncBodyModel();
    }

    /// <summary>
    /// Place the visible <see cref="PlayerModel"/> at the player's <b>feet</b> and
    /// orient it by yaw only, upright — the engine renders the body model at
    /// <c>d->feetpos()</c> with <c>yaw+90</c> and ignores the view pitch
    /// (rendermodel.cpp <c>renderclient</c>). This node stays at the eye, so the model
    /// is offset down by the eye height. The model is a <b>sibling</b> in the same Z-up
    /// world node, so local coordinates match this node's own <c>Position</c>/<c>Basis</c>.
    /// </summary>
    private void SyncBodyModel()
    {
        if (PlayerModel is not { } model) return;
        if (!model.IsInsideTree()) return;
        // Same Z-up frame as this node's Position (= Entity.O, the eye).
        model.Position = Entity.O with { Z = Entity.O.Z - EyeHeight };
        // The imported glb's forward differs from the engine's authored model, so the
        // yaw offset is 180 (not the engine's +90); verified in-engine.
        Vector3 facing = Movement.VecFromYawPitch(Entity.Yaw + 180f, 0f, 1, 0);
        if (facing.LengthSquared() > 1e-6f)
            model.Basis = Basis.LookingAt(facing, new Vector3(0, 0, 1));
    }
}
