using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

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
public partial class MapPlayer : Node3D, IHitscanTarget
{
    public PhysEnt CollisionEntity => Entity;
    public bool IsAlive => Entity.State == PhysEnt.CsAlive && Combat.Health > 0;
    public int Weight => 100;
    public void ApplyHitPush(Vector3 pushVel) => Entity.Vel += pushVel;
    int IHitscanTarget.TakeDamage(int damage)
    {
        TakeDamage(damage);
        return damage;
    }

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

    /// <summary>The local player's first-person hands/weapon, hidden outside first person.</summary>
    public HudgunModel? Hudgun { get; set; }

    /// <summary>Weapon tracer and particle effect renderer.</summary>
    public Effects.WeaponEffectsRenderer? WeaponEffects { get; set; }

    /// <summary>Audio player for weapon fire and player sounds.</summary>
    public AudioStreamPlayer3D? AudioPlayer { get; set; }

    /// <summary>Toggle the detached third-person camera (engine <c>thirdperson</c> bind).</summary>
    public void ToggleThirdPerson()
    {
        ThirdPerson = !ThirdPerson;
        SyncCamera();
    }

    /// <summary>The simulated entity (Sauerbraten Z-up). Read by tests/diagnostics.</summary>
    public PhysEnt Entity { get; } = new();

    /// <summary>Combat and weapon state (fpsgame/game.h:367 <c>fpsstate</c>).</summary>
    public PlayerState Combat { get; } = new();

    /// <summary>Simulation clock in milliseconds (engine <c>lastmillis</c>).</summary>
    public int LastMillis { get; set; }

    /// <summary>View pitch in degrees (the physics model only uses yaw; pitch is camera-only).</summary>
    public float Pitch { get; private set; }

    private OctaPhysics? _phys;
    private Vector2 _mouseDelta; // pending frame delta (screen px)

    // Fixed-step accumulator (seconds of un-simulated time).
    private double _accum;
    private const double StepSeconds = Phys.PhysFrameTime / 1000.0;

    /// <summary>True once <see cref="Setup"/> has run.</summary>
    public bool Initialized { get; private set; }

    public MapPlayer()
    {
        Combat.Respawn();
    }

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
        LastMillis = (int)Time.GetTicksMsec();
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
        else if (@event is InputEventMouseButton mb)
        {
            if (Input.MouseMode != Input.MouseModeEnum.Captured)
            {
                if (mb.Pressed) Input.MouseMode = Input.MouseModeEnum.Captured;
            }
            else
            {
                if (mb.ButtonIndex == MouseButton.Left)
                {
                    Combat.Attacking = mb.Pressed;
                    if (mb.Pressed) TryAttack();
                }
                else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp)
                {
                    NextWeapon(-1);
                }
                else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown)
                {
                    NextWeapon(1);
                }
            }
        }
        else if (@event is InputEventKey { Pressed: true } key && !key.Echo)
        {
            if (key.Keycode >= Key.Key1 && key.Keycode <= Key.Key7)
            {
                int gun = (int)(key.Keycode - Key.Key1);
                SelectWeapon((Gun)gun);
            }
        }
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

        if (Combat.Attacking && Entity.State == PhysEnt.CsAlive)
        {
            if (LastMillis - Combat.LastAction >= Combat.GunWait)
                TryAttack();
        }

        UpdateBodyAnim();
        UpdateHudgunAnim();
    }

    private string _bodyAnim = "";
    private int _lastActionSeen = -1;
    private int _lastPainSeen = -1;

    /// <summary>
    /// Play the body clip matching the current state, mirroring the engine's
    /// <c>renderclient</c> animation selection (rendermodel.cpp:1060-1110).
    /// </summary>
    private void UpdateBodyAnim()
    {
        if (PlayerModel is not { } model) return;
        string name = SelectBodyAnim();
        bool isNewAction = (Combat.LastAction != _lastActionSeen && name.StartsWith("attack "))
                        || (Combat.LastPain != _lastPainSeen && name == "pain");
        if (name != _bodyAnim || isNewAction)
        {
            model.Play(name, loop: IsLoopingAnim(name), restart: isNewAction);
            _bodyAnim = name;
            _lastActionSeen = Combat.LastAction;
            _lastPainSeen = Combat.LastPain;
        }
    }

    /// <summary>Update the first-person hudgun animation.</summary>
    private void UpdateHudgunAnim()
    {
        if (Hudgun is not { } hudgun) return;
        int attackDelay = WeaponCatalog.Get((Gun)Combat.GunSelect).AttackDelay;
        hudgun.UpdateAnim(LastMillis, Combat.LastAction, Combat.LastAttackGun, Combat.GunSelect, attackDelay);
    }

    /// <summary>
    /// Engine anim name for the current player state, faithfully porting
    /// <c>rendermodel.cpp:1060-1110</c> (<c>renderclient</c>) and
    /// <c>render.cpp:133</c> (<c>renderplayer</c>).
    /// </summary>
    public string SelectBodyAnim(int? nowMillis = null)
    {
        int now = nowMillis ?? LastMillis;

        // CS_DEAD: dying for the first 1000 ms after death/lastpain, then dead (looping).
        // rendermodel.cpp:1068-1078
        if (Entity.State == PhysEnt.CsDead)
        {
            int baseTime = Combat.LastPain;
            return (now - baseTime > 1000) ? "dead" : "dying";
        }

        // CS_EDITING / CS_SPECTATOR float (rendermodel.cpp:1080 -> ANIM_EDIT).
        if (Floating) return "edit";

        // CS_LAGGED (rendermodel.cpp:1081 -> ANIM_LAG).
        if (Entity.State == PhysEnt.CsLagged) return "lag";

        // Pain: 300 ms after damage (rendermodel.cpp:1084-1088 -> ANIM_PAIN).
        if (Combat.LastPain > 0 && now - Combat.LastPain < 300)
            return "pain";

        // Attack: active while now - lastaction < attackdelay and lastpain < lastaction.
        // rendermodel.cpp:1089-1093 + render.cpp:133
        int attackDelay = WeaponCatalog.Get((Gun)Combat.GunSelect).AttackDelay + 50;
        if (Combat.LastAction > 0 && Combat.LastPain < Combat.LastAction && now - Combat.LastAction < attackDelay)
        {
            int gun = Combat.LastAttackGun >= Guns.First && Combat.LastAttackGun <= Guns.Last
                ? Combat.LastAttackGun
                : Combat.GunSelect;
            return $"attack {gun + 1}";
        }

        // Locomotion (secondary anim in rendermodel.cpp:1095-1107)
        bool moving = Entity.Move != 0 || Entity.Strafe != 0;

        // In water at/below the falling state → swim (moving or rising) else sink.
        if (Entity.InWater != 0 && Entity.PhysState <= Phys.PhysFall)
            return moving || Entity.Vel.Z > 0 ? "swim" : "sink";

        // Airborne for more than 100 ms → jump (renderclient: timeinair>100).
        if (Entity.TimeInAir > 100) return "jump";

        if (moving)
        {
            if (Entity.Move > 0) return "forward";
            if (Entity.Strafe != 0)
                // NOTE: engine plays the move-backward strafe with ANIM_REVERSE; we
                // play it forward (reverse playback is a later refinement).
                return Entity.Move < 0
                    ? (Entity.Strafe > 0 ? "right" : "left")
                    : (Entity.Strafe > 0 ? "left" : "right");
            if (Entity.Move < 0) return "backward";
        }

        // Hold: rendermodel.cpp:1060 + render.cpp:133.
        // hold = mdl.vwep || d->gunselect==GUN_PISTOL ? 0 : (ANIM_HOLD1+d->gunselect)|ANIM_LOOP.
        // Pistol and models with mdl.vwep fall back to ANIM_IDLE.
        if (Combat.GunSelect >= Guns.First && Combat.GunSelect < (int)Gun.Pistol)
            return $"hold {Combat.GunSelect + 1}";

        return "idle";
    }

    /// <summary>
    /// True if the named clip loops (idle, holds, walks, swims, dead);
    /// false for one-shot clips (jump, dying, pain, attacks).
    /// </summary>
    public static bool IsLoopingAnim(string name) => name switch
    {
        "jump" or "dying" or "pain" => false,
        _ when name.StartsWith("attack ") => false,
        _ => true,
    };

    /// <summary>
    /// Select a weapon (engine <c>gunselect</c>, weapon.cpp). Updates combat state,
    /// swaps third-person view weapon, and switches the first-person hudgun.
    /// </summary>
    public void SelectWeapon(Gun gun)
    {
        int g = (int)gun;
        if (!Guns.IsPlayerSelectable(g)) return;
        Combat.GunSelect = g;
        PlayerModel?.SetViewWeapon(WeaponCatalog.Vweps[g]);
        Hudgun?.SetGun(WeaponCatalog.Get(gun).File);
        UpdateBodyAnim();
    }

    /// <summary>Cycle to next/previous selectable weapon with ammo (weapon.cpp <c>nextweapon</c>).</summary>
    public void NextWeapon(int dir = 1)
    {
        if (Entity.State != PhysEnt.CsAlive) return;
        int step = dir < 0 ? -1 : 1;
        int current = Combat.GunSelect;
        int count = Guns.Last - Guns.First + 1;
        for (int i = 1; i <= count; i++)
        {
            int next = (current + i * step) % count;
            if (next < 0) next += count;
            if (Combat.HasAmmo(next) || next == (int)Gun.Fist)
            {
                SelectWeapon((Gun)next);
                return;
            }
        }
    }

    /// <summary>
    /// Attempt to fire the current weapon (weapon.cpp <c>shoot</c>).
    /// </summary>
    public bool TryAttack(int? nowMillis = null, IReadOnlyList<IHitscanTarget>? targets = null)
    {
        if (Entity.State != PhysEnt.CsAlive)
        {
            // If dead, pressing attack triggers respawn (fps.cpp:376)
            if (Entity.State == PhysEnt.CsDead)
                Respawn();
            return false;
        }

        int now = nowMillis ?? (LastMillis > 0 ? LastMillis : (int)Time.GetTicksMsec());
        LastMillis = now;
        if (now - Combat.LastAction < Combat.GunWait) return false;

        var weapon = WeaponCatalog.Get((Gun)Combat.GunSelect);
        if (Combat.GunSelect != (int)Gun.Fist && !Combat.HasAmmo(Combat.GunSelect))
        {
            Combat.GunWait = 600; // weapon.cpp:788 msgsound(S_NOAMMO); d->gunwait = 600;
            NextWeapon();
            return false;
        }

        Combat.LastAction = now;
        Combat.LastAttackGun = Combat.GunSelect;
        Combat.GunWait = weapon.AttackDelay;
        if (Combat.GunSelect != (int)Gun.Fist && Combat.Ammo[Combat.GunSelect] > 0)
            Combat.Ammo[Combat.GunSelect]--;

        // Hitscan raycast & resolution (weapon.cpp:775 shoot)
        // Fist and hitscan weapons (projspeed == 0) execute hitscan raycast.
        if (weapon.ProjSpeed == 0)
        {
            Vector3 from = Entity.O;
            Vector3 forward = Movement.VecFromYawPitch(Entity.Yaw, Pitch, 1, 0);
            if (forward.LengthSquared() < 1e-6f) forward = new Vector3(0f, 1f, 0f);
            float maxRange = weapon.Range > 0 ? weapon.Range : 2048f;
            Vector3 targetAim = from + forward.Normalized() * maxRange;

            targets ??= Array.Empty<IHitscanTarget>();
            var hitscan = WeaponHitscan.Fire(
                _phys?.Octa,
                from,
                targetAim,
                weapon,
                Combat.QuadMillis > 0,
                targets);

            // Apply shooter recoil / kickback directly to entity velocity (weapon.cpp:799)
            Entity.Vel += hitscan.KickbackVel;

            // Apply damage and push to struck targets
            for (int i = 0; i < hitscan.Hits.Count; i++)
            {
                var hit = hitscan.Hits[i];
                hit.Target.ApplyHitPush(hit.PushVel);
                hit.Target.TakeDamage(hit.Damage);
            }

            // Spawn visual tracer beams (weapon.cpp:592 shoteffects)
            WeaponEffects?.AddShotEffects((Gun)Combat.GunSelect, from, hitscan.RayEndpoints);
        }

        UpdateBodyAnim();
        UpdateHudgunAnim();
        return true;
    }

    /// <summary>Apply damage to the player, updating health and lastpain timestamp.</summary>
    public void TakeDamage(int damage, int? nowMillis = null)
    {
        int now = nowMillis ?? (LastMillis > 0 ? LastMillis : (int)Time.GetTicksMsec());
        LastMillis = now;
        Combat.LastPain = now;
        Combat.DoDamage(damage);
        if (Combat.Health <= 0)
        {
            Combat.Health = 0;
            Entity.State = PhysEnt.CsDead;
            Entity.Dead = true;
        }
        UpdateBodyAnim();
    }

    /// <summary>Respawn the player after death.</summary>
    public void Respawn()
    {
        Combat.Respawn();
        Entity.State = PhysEnt.CsAlive;
        Entity.Dead = false;
        _bodyAnim = "";
        UpdateBodyAnim();
        UpdateHudgunAnim();
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
            LastMillis += Phys.PhysFrameTime;
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
        if (Hudgun is { } hudgun) hudgun.Visible = !ThirdPerson && !Floating;

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
        _bodyAnim = ""; // force a re-select against the (possibly new) model
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
