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
    int IExplosionTarget.TakeDamage(int damage)
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

    /// <summary>Original <c>invmouse</c> (rendergl.cpp:772): invert the vertical look axis.</summary>
    [Export] public bool InvertMouse { get; set; } = false;

    /// <summary>
    /// Apply persisted user look settings (engine <c>sensitivity</c>,
    /// <c>sensitivityscale</c>, <c>mouseaccel</c>, <c>invmouse</c>, <c>fov</c> —
    /// rendergl.cpp) and refresh the camera FOV. Values are assumed already clamped
    /// by <see cref="UserSettings"/>.
    /// </summary>
    public void ApplyLookSettings(UserSettings settings)
    {
        Sensitivity = settings.Sensitivity;
        SensitivityScale = settings.SensitivityScale;
        MouseAccel = settings.MouseAccel;
        InvertMouse = settings.InvertMouse;
        Fov = settings.Fov;
        if (GetNodeOrNull<Camera3D>("Camera3D") is { } cam) ApplyFov(cam);
    }

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

    /// <summary>Active game mode info for this player.</summary>
    public GameModeInfo? Mode { get; set; }

    /// <summary>Saved spawn position at player's feet (physics.cpp entinmap).</summary>
    public Vector3 SpawnFeet { get; set; }

    /// <summary>Saved spawn yaw in degrees.</summary>
    public float SpawnYaw { get; set; }

    /// <summary>Minimum wait (ms) after death before respawn is allowed (fpsgame/fps.cpp:354).</summary>
    [Export] public int SpawnWaitMillis { get; set; } = 500;

    /// <summary>Default weapon selected on spawn/respawn.</summary>
    [Export] public Gun DefaultGun { get; set; } = Gun.Pistol;

    /// <summary>Active item pickups manager for this map/session.</summary>
    public ItemManager? Items { get; set; }

    /// <summary>Active jump pad trigger entities in this map/session.</summary>
    public IReadOnlyList<JumpPad>? JumpPads { get; set; }
    public IReadOnlyList<Teleport>? Teleports { get; set; }

    /// <summary>
    /// The visible body model, if any. It must be a <b>sibling</b> of this node (a child
    /// of the same Z-up world node), so that it can be placed at the feet and kept
    /// upright independently of the camera's view pitch.
    /// </summary>
    public PlayerModel? PlayerModel { get; set; }

    /// <summary>The local player's first-person hands/weapon, hidden outside first person.</summary>
    public HudgunModel? Hudgun { get; set; }

    private Effects.WeaponEffectsRenderer? _weaponEffects;

    /// <summary>Weapon tracer and particle effect renderer.</summary>
    public Effects.WeaponEffectsRenderer? WeaponEffects
    {
        get => _weaponEffects;
        set
        {
            _weaponEffects = value;
            if (_weaponEffects != null)
            {
                _weaponEffects.ActiveRocketsProvider = () => ActiveRockets;
            }
        }
    }

    /// <summary>Audio player for weapon fire and player sounds in 3D (for remote / 3rd person).</summary>
    public AudioStreamPlayer3D? AudioPlayer { get; set; }

    /// <summary>Audio player for local player sounds (non-spatialized, direct to ears).</summary>
    public AudioStreamPlayer? AudioPlayer2D { get; set; }

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

    /// <summary>Player team index (0 = FFA, 1 = blue, 2 = red).</summary>
    [Export]
    public int Team
    {
        get => PlayerState.TeamFlag(Combat.Team);
        set => Combat.Team = PlayerState.FlagTeam(value);
    }

    /// <summary>Player team name (e.g. "good" or "evil"). Delegates to Combat.Team.</summary>
    public string TeamName
    {
        get => Combat.Team;
        set => Combat.Team = value;
    }

    /// <summary>Timestamp (ms) of the last successful hit on an enemy (engine <c>lasthit</c> in fps.cpp).</summary>
    public int LastHit { get; set; }

    /// <summary>Simulation clock in milliseconds (engine <c>lastmillis</c>).</summary>
    public int LastMillis { get; set; }

    /// <summary>Last entity type picked up or triggered (fpsgame/game.h:549 <c>lastpickup</c>).</summary>
    public int LastPickup { get; set; } = -1;

    /// <summary>Timestamp (ms) of the last entity pickup or trigger (fpsgame/game.h:549 <c>lastpickupmillis</c>).</summary>
    public int LastPickupMillis { get; set; }

    /// <summary>View pitch in degrees (the physics model only uses yaw; pitch is camera-only).</summary>
    public float Pitch { get; private set; }

    private OctaPhysics? _phys;
    private Vector2 _mouseDelta; // pending frame delta (screen px)

    // Fixed-step accumulator (seconds of un-simulated time).
    private double _accum;
    private const double StepSeconds = Phys.PhysFrameTime / 1000.0;

    /// <summary>Active flying rocket projectiles fired by or tracked by this player/scene.</summary>
    public List<RocketProjectile> ActiveRockets { get; } = new();

    /// <summary>Active bounce grenades fired by or tracked by this player/scene.</summary>
    public List<BounceBody> ActiveGrenades { get; } = new();

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

        if (AudioPlayer2D == null)
        {
            AudioPlayer2D = new AudioStreamPlayer { Name = "AudioPlayer2D" };
            AddChild(AudioPlayer2D);
        }

        if (AudioPlayer == null)
        {
            AudioPlayer = new AudioStreamPlayer3D { Name = "AudioPlayer3D" };
            AddChild(AudioPlayer);
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
        SpawnFeet = spawnFeet;
        SpawnYaw = yawDegrees;
        Entity.Type = Phys.EntPlayer;
        Entity.O = spawnFeet with { Z = spawnFeet.Z + EyeHeight };
        Entity.Yaw = yawDegrees;
        Pitch = 0f;
        LastMillis = (int)Time.GetTicksMsec();
        Initialized = true;
        _phys.DynEnts.Add(Entity);
        Combat.Spawn(Mode ?? GameModeCatalog.Get("ffa"));
        SelectWeapon(Combat.GunSelect >= Guns.First && Combat.GunSelect <= Guns.Last ? (Gun)Combat.GunSelect : DefaultGun);
        SyncTransform();
        if (GetNodeOrNull<Camera3D>("Camera3D") is { } cam) ApplyFov(cam);
    }

    public override void _ExitTree()
    {
        _phys?.DynEnts.Remove(Entity);
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
                    if (mb.Pressed && Entity.State == PhysEnt.CsDead)
                    {
                        TryRespawn();
                        return;
                    }
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
        if (Entity.State != PhysEnt.CsDead)
        {
            Entity.Move = (Input.IsActionPressed("move_forward") ? 1 : 0) - (Input.IsActionPressed("move_back") ? 1 : 0);
            Entity.Strafe = (Input.IsActionPressed("strafe_right") ? 1 : 0) - (Input.IsActionPressed("strafe_left") ? 1 : 0);
            Entity.Jumping = Input.IsActionPressed("jump");
        }
        else
        {
            Entity.Move = 0;
            Entity.Strafe = 0;
            Entity.Jumping = false;
            if (Input.IsActionJustPressed("jump")) TryRespawn();
        }

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
        PlayerModel?.SetViewWeapon(WeaponCatalog.Vwep(gun));
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
        int now = nowMillis ?? (LastMillis > 0 ? LastMillis : (int)Time.GetTicksMsec());
        if (Entity.State != PhysEnt.CsAlive)
        {
            // If dead, pressing attack triggers respawn (fps.cpp:376)
            if (Entity.State == PhysEnt.CsDead)
                TryRespawn(now);
            return false;
        }

        LastMillis = now;
        if (now - Combat.LastAction < Combat.GunWait) return false;

        var weapon = WeaponCatalog.Get((Gun)Combat.GunSelect);
        if (Combat.GunSelect != (int)Gun.Fist && !Combat.HasAmmo(Combat.GunSelect))
        {
            Combat.GunWait = 600; // weapon.cpp:788 msgsound(S_NOAMMO); d->gunwait = 600;
            PlaySound(GameSound.NoAmmo);
            NextWeapon();
            return false;
        }

        Combat.LastAction = now;
        Combat.LastAttackGun = Combat.GunSelect;
        Combat.GunWait = weapon.AttackDelay;
        if (Combat.GunSelect != (int)Gun.Fist && Combat.Ammo[Combat.GunSelect] > 0)
            Combat.Ammo[Combat.GunSelect]--;

        // Play weapon firing sound (weapon.cpp:672 playsound(sound))
        PlaySound(weapon.Sound);

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

            // Play hit confirmation sound and record LastHit timestamp if any target was hit (fpsgame/weapon.cpp:357-358)
            if (hitscan.Hits.Count > 0)
            {
                LastHit = LastMillis;
                PlaySound(GameSound.Hit);
            }

            // Spawn visual tracer beams (weapon.cpp:592 shoteffects)
            Gun gun = (Gun)Combat.GunSelect;
            WeaponEffects?.AddShotEffects(gun, from, hitscan.RayEndpoints);

            // Spawn bullet decals for ray endpoints that did not hit a candidate player/entity (weapon.cpp:752, 772)
            if (WeaponEffects != null && gun != Gun.Fist && gun != Gun.Bite)
            {
                float decalRadius = gun == Gun.Rifle ? 3.0f : 2.0f;
                // If there were no target hits, all ray endpoints hit world/geometry
                if (hitscan.Hits.Count == 0)
                {
                    for (int i = 0; i < hitscan.RayEndpoints.Count; i++)
                    {
                        Vector3 hitPos = hitscan.RayEndpoints[i];
                        Vector3 dir = (from - hitPos).Normalized();
                        WeaponEffects.AddBulletHole(hitPos, dir, decalRadius);
                    }
                }
                else
                {
                    // For multiple rays (e.g. shotgun), spawn decals for endpoints that didn't hit a target
                    for (int i = 0; i < hitscan.RayEndpoints.Count; i++)
                    {
                        Vector3 hitPos = hitscan.RayEndpoints[i];
                        bool hitTarget = false;
                        for (int h = 0; h < hitscan.Hits.Count; h++)
                        {
                            if (hitscan.Hits[h].HitPos.IsEqualApprox(hitPos))
                            {
                                hitTarget = true;
                                break;
                            }
                        }
                        if (!hitTarget)
                        {
                            Vector3 dir = (from - hitPos).Normalized();
                            WeaponEffects.AddBulletHole(hitPos, dir, decalRadius);
                        }
                    }
                }
            }
        }
        else if ((Gun)Combat.GunSelect == Gun.RocketLauncher)
        {
            // Rocket Launcher (weapon.cpp:628, 636)
            Vector3 from = Entity.O;
            Vector3 forward = Movement.VecFromYawPitch(Entity.Yaw, Pitch, 1, 0);
            if (forward.LengthSquared() < 1e-6f) forward = new Vector3(0f, 1f, 0f);
            float maxRange = weapon.Range > 0 ? weapon.Range : 1024f;
            Vector3 targetAim = from + forward.Normalized() * maxRange;

            // Recoil kickback (weapon.cpp:798)
            Entity.Vel += forward.Normalized() * (weapon.KickAmount * -2.5f);

            var rocket = new RocketProjectile(
                from,
                targetAim,
                weapon,
                owner: this,
                world: _phys?.Octa,
                quadDamage: Combat.QuadMillis > 0);

            rocket.Detonated += (impactPos, directVictim, splashResult) =>
            {
                if (splashResult.Hits.Count > 0 || directVictim != null)
                {
                    LastHit = LastMillis;
                    PlaySound(GameSound.Hit);
                }
                PlaySound(GameSound.RlHit);
                WeaponEffects?.AddExplosionEffect(Gun.RocketLauncher, impactPos, weapon.ExpRad);
                WeaponEffects?.AddScorchMark(impactPos, -rocket.Dir, weapon.ExpRad * 0.5f);
            };

            ActiveRockets.Add(rocket);
        }
        else if ((Gun)Combat.GunSelect == Gun.GrenadeLauncher)
        {
            // Grenade Launcher (weapon.cpp:639-648, 796-807)
            Vector3 from = Entity.O;
            Vector3 forward = Movement.VecFromYawPitch(Entity.Yaw, Pitch, 1, 0);
            if (forward.LengthSquared() < 1e-6f) forward = new Vector3(0f, 1f, 0f);
            Vector3 dir = forward.Normalized();
            float maxRange = weapon.Range > 0 ? weapon.Range : 1024f;
            Vector3 to = from + dir * maxRange;
            float dist = from.DistanceTo(to);

            // Recoil kickback (weapon.cpp:798)
            Entity.Vel += dir * (weapon.KickAmount * -2.5f);

            float shorten = 0f;
            if (weapon.Range > 0 && dist > weapon.Range)
                shorten = weapon.Range;
            if (_phys?.Octa != null)
            {
                float barrier = OctaRaycast.RayCube(
                    _phys.Octa,
                    from,
                    dir,
                    dist,
                    OctaRaycast.RayClipMat | OctaRaycast.RayAlphaPoly,
                    0,
                    out _);
                if (barrier > 0f && barrier < dist && (shorten == 0f || barrier < shorten))
                    shorten = barrier;
            }
            if (shorten > 0f) to = from + dir * shorten;

            // In weapon.cpp:641-643: up = to; up.z += dist/8;
            float hitDist = from.DistanceTo(to);
            Vector3 up = to with { Z = to.Z + hitDist / 8.0f };
            Vector3 launchDir = (up - from).Normalized();
            Vector3 launchVel = launchDir * (float)weapon.ProjSpeed;

            if (_phys != null)
            {
                var grenade = new BounceBody
                {
                    BodyKind = BounceBody.Kind.Grenade,
                    AutoAdvance = false
                };
                grenade.Setup(
                    _phys,
                    from,
                    launchVel,
                    yawDegrees: Entity.Yaw,
                    owner: this,
                    gun: weapon,
                    quadDamage: Combat.QuadMillis > 0,
                    lifeTime: weapon.Ttl > 0 ? weapon.Ttl : 1500);

                grenade.Detonated += (impactPos, splashResult) =>
                {
                    if (splashResult.Hits.Count > 0)
                    {
                        LastHit = LastMillis;
                        PlaySound(GameSound.Hit);
                    }
                    PlaySound(GameSound.FExplode);
                    WeaponEffects?.AddExplosionEffect(Gun.GrenadeLauncher, impactPos, weapon.ExpRad);
                    WeaponEffects?.AddScorchMark(impactPos, new Vector3(0f, 0f, 1f), weapon.ExpRad * 0.5f);
                };

                GetParent()?.AddChild(grenade);
                ActiveGrenades.Add(grenade);
            }
        }

        UpdateBodyAnim();
        UpdateHudgunAnim();
        return true;
    }

    /// <summary>Apply damage to the player, updating health and lastpain timestamp.</summary>
    public void TakeDamage(int damage, int? nowMillis = null)
    {
        if (Entity.State == PhysEnt.CsDead) return;

        int now = nowMillis ?? (LastMillis > 0 ? LastMillis : (int)Time.GetTicksMsec());
        LastMillis = now;
        Combat.LastPain = now;
        Combat.DoDamage(damage);
        if (Combat.Health <= 0)
        {
            Die(now);
        }
        else
        {
            UpdateBodyAnim();
        }
    }

    /// <summary>Kill the player (engine <c>deathstate</c>, fpsgame/fps.cpp:425).</summary>
    public void Die(int? nowMillis = null)
    {
        int now = nowMillis ?? (LastMillis > 0 ? LastMillis : (int)Time.GetTicksMsec());
        LastMillis = now;
        Combat.Health = 0;
        Combat.LastPain = now;
        Combat.Attacking = false;
        Entity.State = PhysEnt.CsDead;
        Entity.Dead = true;
        Entity.Move = 0;
        Entity.Strafe = 0;
        Entity.Jumping = false;

        PlaySound(Random.Shared.Next(2) == 0 ? GameSound.Die1 : GameSound.Die2);
        UpdateBodyAnim();
    }

    /// <summary>
    /// Attempt to respawn after death if the required minimum delay has elapsed
    /// (<c>fpsgame/fps.cpp:354</c>).
    /// </summary>
    public bool TryRespawn(int? nowMillis = null)
    {
        if (Entity.State != PhysEnt.CsDead) return false;

        int now = nowMillis ?? (LastMillis > 0 ? LastMillis : (int)Time.GetTicksMsec());
        if (now - Combat.LastPain < SpawnWaitMillis) return false;

        Respawn();
        return true;
    }

    /// <summary>
    /// Respawn the player after death, resetting combat state, loadout, position,
    /// and velocity to the spawn point (<c>fpsgame/fps.cpp:350 respawnself</c>).
    /// </summary>
    public void Respawn(Vector3? spawnFeet = null, float? yawDegrees = null)
    {
        Vector3 feet = spawnFeet ?? SpawnFeet;
        float yaw = yawDegrees ?? SpawnYaw;

        Entity.O = feet with { Z = feet.Z + EyeHeight };
        Entity.Yaw = yaw;
        Entity.Vel = Vector3.Zero;
        Entity.Falling = Vector3.Zero;
        Entity.Floor = new Vector3(0, 0, 1);
        Entity.PhysState = Phys.PhysFall;
        Entity.TimeInAir = 0;
        Entity.State = PhysEnt.CsAlive;
        Entity.Dead = false;
        Entity.Blocked = false;

        Combat.Spawn(Mode ?? GameModeCatalog.Get("ffa"));
        LastHit = 0;
        LastPickup = -1;
        LastPickupMillis = 0;
        _bodyAnim = "";
        SelectWeapon(Combat.GunSelect >= Guns.First && Combat.GunSelect <= Guns.Last ? (Gun)Combat.GunSelect : DefaultGun);
        SyncTransform();
        UpdateBodyAnim();
        UpdateHudgunAnim();
    }

    /// <summary>
    /// Advance the simulation by <paramref name="delta"/> seconds of real time,
    /// running whole fixed 5 ms steps caught up by count (a partial step is kept
    /// in the accumulator). Input is taken from <see cref="Entity"/> — set it first.
    /// Also advances active projectiles (rockets and grenades).
    /// </summary>
    public void Advance(double delta, IReadOnlyList<IExplosionTarget>? targets = null)
    {
        if (!Initialized || _phys == null) return;
        Entity.Pitch = Pitch; // pitch feeds movement only while floating (vecfromyawpitch)
        _accum += delta;
        int guard = 0;
        var candidates = targets ?? new IExplosionTarget[] { this };
        while (_accum >= StepSeconds && guard++ < 100)
        {
            _phys.MovePlayer(Entity, Phys.MoveRes, Phys.PhysFrameTime);
            LastMillis += Phys.PhysFrameTime;
            _accum -= StepSeconds;

            if (Entity.Dead && Entity.State != PhysEnt.CsDead)
            {
                Die(LastMillis);
            }

            if (Combat.CheckQuad(Phys.PhysFrameTime))
            {
                PlaySound(GameSound.PupOut);
            }

            Items?.CheckPickups(this, LastMillis);
            CheckJumpPads(LastMillis);
            CheckTeleports(LastMillis);

            // Advance active flying rockets (weapon.cpp:523 updateprojectiles)
            for (int i = ActiveRockets.Count - 1; i >= 0; i--)
            {
                var rocket = ActiveRockets[i];
                if (rocket.Advance(Phys.PhysFrameTime, _phys.Octa, candidates))
                {
                    ActiveRockets.RemoveAt(i);
                }
            }

            // Advance active grenades (weapon.cpp:243 updatebouncers)
            for (int i = ActiveGrenades.Count - 1; i >= 0; i--)
            {
                var grenade = ActiveGrenades[i];
                if (grenade.Exploded)
                {
                    ActiveGrenades.RemoveAt(i);
                }
                else
                {
                    grenade.Advance(StepSeconds, candidates);
                    if (grenade.Exploded)
                    {
                        ActiveGrenades.RemoveAt(i);
                    }
                }
            }
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
        Pitch -= d.Y * cursens * (InvertMouse ? -1f : 1f);
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
        {
            Vector3 up = new Vector3(0, 0, 1);
            if (Mathf.Abs(forward.Dot(up)) > 0.999f)
            {
                float rad = Mathf.DegToRad(Entity.Yaw);
                up = new Vector3(-Mathf.Sin(rad), Mathf.Cos(rad), 0);
            }
            Basis = Basis.LookingAt(forward, up);
        }
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

    /// <summary>
    /// Checks all active jump pads against the player's feet position, triggering the first reachable pad
    /// (<c>fpsgame/entities.cpp:342 checkitems</c>).
    /// </summary>
    public JumpPad? CheckJumpPads(int lastMillis)
    {
        if (!IsAlive || Floating || JumpPads == null) return null;

        Vector3 feet = Entity.FeetPos();
        for (int i = 0; i < JumpPads.Count; i++)
        {
            var pad = JumpPads[i];
            float dist = pad.Position.DistanceTo(feet);
            if (dist < 12.0f)
            {
                if (ApplyJumpPad(pad, lastMillis))
                    return pad;
            }
        }
        return null;
    }

    /// <summary>
    /// Applies a jump pad impulse and sound effects (<c>fpsgame/entities.cpp:317 trypickup / jumppadeffects</c>).
    /// Returns false if rate-limited by the 300 ms cooldown.
    /// </summary>
    public bool ApplyJumpPad(JumpPad pad, int lastMillis)
    {
        if (LastPickup == EntityType.JumpPad && lastMillis - LastPickupMillis < 300)
            return false;

        LastPickup = EntityType.JumpPad;
        LastPickupMillis = lastMillis;

        // Sound effect (fpsgame/entities.cpp:231 jumppadeffects)
        if (pad.Sound >= 0)
        {
            PlaySound(GameSound.JumpPad);
        }

        Entity.Falling = Vector3.Zero;
        Entity.PhysState = Phys.PhysFall;
        Entity.TimeInAir = 1;
        Entity.Vel = pad.Push;

        return true;
    }

    /// <summary>
    /// Checks all teleporters against the player's feet position (<c>fpsgame/entities.cpp:334 checkitems</c>).
    /// Teleporters use radius 16.
    /// </summary>
    public Teleport? CheckTeleports(int lastMillis)
    {
        if (!IsAlive || Floating || Teleports == null) return null;

        Vector3 feet = Entity.FeetPos();
        for (int i = 0; i < Teleports.Count; i++)
        {
            var teleport = Teleports[i];
            float dist = teleport.BasePosition.DistanceTo(feet);
            if (dist < 16.0f)
            {
                if (ApplyTeleport(teleport, lastMillis))
                    return teleport;
            }
        }
        return null;
    }

    /// <summary>
    /// Applies teleportation, destination positioning, yaw orientation, velocity preservation, and sound effects
    /// (<c>fpsgame/entities.cpp:253-281 teleport, 202-227 teleporteffects, 296-308 trypickup</c>).
    /// Returns false if rate-limited (500 ms cooldown) or missing destination.
    /// </summary>
    public bool ApplyTeleport(Teleport teleport, int lastMillis)
    {
        if (LastPickup == EntityType.Teleport && lastMillis - LastPickupMillis < 500)
            return false;

        var dest = teleport.Destination;
        if (dest == null) return false;

        LastPickup = EntityType.Teleport;
        LastPickupMillis = lastMillis;

        // Sound effect (fpsgame/entities.cpp:202 teleporteffects)
        if (teleport.Sound >= 0)
        {
            PlaySound(GameSound.Teleport);
        }

        // Apply position: dest is ground level; eye pos = dest + eyeheight
        Entity.O = new Vector3(dest.Position.X, dest.Position.Y, dest.Position.Z + EyeHeight);
        Entity.Yaw = dest.Yaw;

        if (dest.Pushed)
        {
            Vector3 dir = Movement.VecFromYawPitch(Entity.Yaw, 0f, 1, 0);
            float speed = new Vector2(Entity.Vel.X, Entity.Vel.Y).Length();
            Entity.Vel = new Vector3(dir.X * speed, dir.Y * speed, Entity.Vel.Z);
        }
        else
        {
            Entity.Vel = Vector3.Zero;
        }

        SyncTransform();
        return true;
    }

    /// <summary>
    /// Play a game sound. If local player, uses non-positional <see cref="AudioPlayer2D"/>;
    /// otherwise plays through 3D <see cref="AudioPlayer"/>.
    /// </summary>
    public void PlaySound(GameSound sound)
    {
        if (sound == GameSound.None) return;
        if (AudioPlayer2D != null)
        {
            AudioPlayer2D.Play(sound);
        }
        else if (AudioPlayer != null)
        {
            AudioPlayer.Play3D(sound);
        }
    }
}
