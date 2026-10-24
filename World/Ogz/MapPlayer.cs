using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;
using HudgunModel = SauerbratenRevival.World.Models.HudgunModel;
using PlayerModel = SauerbratenRevival.World.Models.PlayerModel;

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
    [Signal]
    public delegate void HealthChangedEventHandler(int oldHealth, int newHealth);

    [Signal]
    public delegate void DiedEventHandler();

    [Signal]
    public delegate void RespawnedEventHandler();

    [Signal]
    public delegate void WeaponChangedEventHandler(int gunId);

    [Signal]
    public delegate void DamageDealtEventHandler(int targetId, int damage);

    [Signal]
    public delegate void ModeChangedEventHandler(GameModeInfo newMode);

    public PhysEnt CollisionEntity => Entity;
    public bool IsAlive => Entity.State == PhysEnt.CsAlive;
    public int Weight => 100;
    public void ApplyHitPush(Vector3 pushVel) => Entity.Vel += pushVel;
    int IExplosionTarget.TakeDamage(int damage)
    {
        TakeDamage(damage);
        return damage;
    }

    private PlayerView? _view;

    /// <summary>Presentation controller managing camera, view offsets, and animations.</summary>
    public PlayerView View
    {
        get
        {
            if (_view == null)
            {
                _view = GetNodeOrNull<PlayerView>("PlayerView") ?? new PlayerView(this);
                if (_view.GetParent() == null)
                {
                    AddChild(_view);
                }
            }
            return _view;
        }
        set => _view = value;
    }

    /// <summary>
    /// Original <c>fov</c> (rendergl.cpp VARP, default 100). This is the
	/// <b>horizontal</b> field of view; it is converted to Godot's vertical FOV via
	/// the engine's <c>fovy = 2*atan(tan(fov/2)/aspect)</c> (rendergl.cpp:1872).
    /// </summary>
    [Export]
    public float Fov
    {
        get => View.Fov;
        set => View.Fov = value;
    }

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
        View.ApplyLookSettings(settings);
    }

    /// <summary>Spawn eye offset above the spawn point's feet (ents.h <c>eyeheight</c>).</summary>
    public const float EyeHeight = 14.0f;

    /// <summary>
    /// Third-person camera distance behind the eye (engine <c>thirdpersondistance</c>, rendergl.cpp).
    /// <c>0</c> disables the offset.
    /// </summary>
    [Export]
    public float ThirdPersonDistance
    {
        get => View.ThirdPersonDistance;
        set => View.ThirdPersonDistance = value;
    }

    /// <summary>
    /// Third-person vertical offset (engine <c>thirdpersonup</c>, rendergl.cpp). Negative
    /// looks down at the player; <c>0</c> keeps the eye height while pulling the camera in.
    /// </summary>
    [Export]
    public float ThirdPersonUp
    {
        get => View.ThirdPersonUp;
        set => View.ThirdPersonUp = value;
    }

    /// <summary>True while the camera is detached from the player's eye (engine <c>isthirdperson()</c>).</summary>
    public bool ThirdPerson => View.ThirdPerson;

    /// <summary>
    /// When true, the placeholder <c>Body</c> capsule is never shown — the scene
    /// supplies a real <see cref="PlayerModel"/> instead (set by the viewer when it
    /// loads a converted body model).
    /// </summary>
    public bool HidePlaceholderBody
    {
        get => View.HidePlaceholderBody;
        set => View.HidePlaceholderBody = value;
    }

    private GameModeInfo _mode = GameModeCatalog.Default;

    /// <summary>
    /// Active game mode info for this player.
    /// </summary>
    public GameModeInfo Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            EmitSignal(SignalName.ModeChanged, value);
        }
    }

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

    private Game.FlagManager? _flags;

    /// <summary>Active flag manager for CTF / Protect / Hold modes.</summary>
    public Game.FlagManager? Flags
    {
        get => _flags;
        set
        {
            if (ReferenceEquals(_flags, value)) return;
            if (_flags != null) _flags.OnAudioEvent -= OnFlagAudioEvent;
            _flags = value;
            if (_flags != null) _flags.OnAudioEvent += OnFlagAudioEvent;
        }
    }

    /// <summary>
    /// Plays flag state-machine audio for the local player (ctf.h:852-968
    /// <c>teamsound</c> / <c>playsound</c>). Every flag sound is non-positional in
    /// the original (no location is passed), so it is played straight to the ears.
    /// A flag scored by the enemy team is heard as <see cref="GameSound.FlagFail"/>
    /// rather than <see cref="GameSound.FlagScore"/> (ctf.h:952).
    /// </summary>
    private void OnFlagAudioEvent(FlagAudioEvent evt)
    {
        GameSound sound = evt.Sound;
        if (sound == GameSound.FlagScore && evt.Team != Team)
            sound = GameSound.FlagFail;
        PlaySound(sound);
    }

    /// <summary>Local player/client ID (default 0).</summary>
    public int PlayerId { get; set; } = 0;

    /// <summary>Active jump pad trigger entities in this map/session.</summary>
    public IReadOnlyList<JumpPad>? JumpPads { get; set; }
    public IReadOnlyList<Teleport>? Teleports { get; set; }

    /// <summary>
    /// The visible body model, if any. It must be a <b>sibling</b> of this node (a child
    /// of the same Z-up world node), so that it can be placed at the feet and kept
    /// upright independently of the camera's view pitch.
    /// </summary>
    public PlayerModel? PlayerModel
    {
        get => View.PlayerModel;
        set => View.PlayerModel = value;
    }

    /// <summary>The local player's first-person hands/weapon, hidden outside first person.</summary>
    public HudgunModel? Hudgun
    {
        get => View.Hudgun;
        set => View.Hudgun = value;
    }

    private Effects.WeaponEffectsRenderer? _weaponEffects;

    /// <summary>
    /// Weapon tracer and particle effect renderer. Reads the parent
    /// <see cref="MapWorld.WeaponEffects"/> when attached to a world, or a standalone
    /// instance assigned directly.
    /// </summary>
    public Effects.WeaponEffectsRenderer? WeaponEffects
    {
        get => (GetParent() as MapWorld)?.WeaponEffects ?? _weaponEffects;
        set
        {
            _weaponEffects = value;
            if (_weaponEffects != null)
            {
                _weaponEffects.ActiveRockets = _standaloneRockets;
            }
        }
    }

    /// <summary>Audio player for weapon fire and player sounds in 3D (for remote / 3rd person).</summary>
    public AudioStreamPlayer3D? AudioPlayer { get; set; }

    /// <summary>Audio player for local player sounds (non-spatialized, direct to ears).</summary>
    public AudioStreamPlayer? AudioPlayer2D { get; set; }

    /// <summary>Toggle the detached third-person camera (engine <c>thirdperson</c> bind).</summary>
    public void ToggleThirdPerson() => View.ToggleThirdPerson();

    /// <summary>The simulated entity (Sauerbraten Z-up). Read by tests/diagnostics.</summary>
    public PhysEnt Entity { get; } = new();

    /// <summary>Combat and weapon state (fpsgame/game.h:367 <c>fpsstate</c>).</summary>
    public PlayerState Combat { get; } = new();

    /// <summary>
    /// Current player health (<c>fpsstate::health</c>). Setting this property emits
    /// <see cref="SignalName.HealthChanged"/> when the value changes.
    /// </summary>
    public int Health
    {
        get => Combat.Health;
        set => Combat.Health = value;
    }

    /// <summary>
    /// Currently selected weapon ID (<c>fpsstate::gunselect</c>). Setting this property
    /// updates weapon models and emits <see cref="SignalName.WeaponChanged"/>.
    /// </summary>
    public int GunSelect
    {
        get => Combat.GunSelect;
        set
        {
            if (Guns.IsPlayerSelectable(value))
            {
                SelectWeapon((Gun)value);
            }
            else
            {
                Combat.GunSelect = value;
            }
        }
    }

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
    public int LastHit
    {
        get => Combat.LastHit;
        set => Combat.LastHit = value;
    }

    private readonly SimClock _standaloneClock = new();

    /// <summary>
    /// Shared simulation clock when this player is registered with a
    /// <see cref="MapWorld"/>; null when running standalone (tests/harness).
    /// </summary>
    public SimClock? Clock { get; private set; }

    /// <summary>
    /// Simulation clock in milliseconds (engine <c>lastmillis</c>). Reads the shared
    /// world clock when registered, otherwise a private standalone clock so isolated
    /// tests can still drive time explicitly. The setter only drives the standalone
    /// clock — an attached player can never move the world's clock.
    /// </summary>
    public int LastMillis
    {
        get => (Clock ?? _standaloneClock).NowMillis;
        set => _standaloneClock.Set(value);
    }

    /// <summary>Attach this player to the shared world clock (called on registration).</summary>
    public void AttachClock(SimClock clock) => Clock = clock;

    /// <summary>Detach from the shared world clock, preserving its current value.</summary>
    public void DetachClock()
    {
        if (Clock is { } clock) _standaloneClock.Set(clock.NowMillis);
        Clock = null;
    }

    /// <summary>Last entity type picked up or triggered (fpsgame/game.h:549 <c>lastpickup</c>).</summary>
    public int LastPickup
    {
        get => Combat.LastPickup;
        set => Combat.LastPickup = value;
    }

    /// <summary>Timestamp (ms) of the last entity pickup or trigger (fpsgame/game.h:549 <c>lastpickupmillis</c>).</summary>
    public int LastPickupMillis
    {
        get => Combat.LastPickupMillis;
        set => Combat.LastPickupMillis = value;
    }

    /// <summary>View pitch in degrees, delegating directly to <see cref="PhysEnt.Pitch"/>.</summary>
    public float Pitch
    {
        get => Entity.Pitch;
        private set => Entity.Pitch = value;
    }

    private OctaPhysics? _phys;
    /// <summary>The world physics instance used by this player.</summary>
    public OctaPhysics? Physics => _phys;
    private Vector2 _mouseDelta; // pending frame delta (screen px)

    private readonly List<RocketProjectile> _standaloneRockets = new();
    private readonly List<BounceBody> _standaloneGrenades = new();

    /// <summary>Active flying rocket projectiles fired by or tracked by this player/scene.</summary>
    public List<RocketProjectile> ActiveRockets => (GetParent() as MapWorld)?.ActiveRockets ?? _standaloneRockets;

    /// <summary>Active bounce grenades fired by or tracked by this player/scene.</summary>
    public List<BounceBody> ActiveGrenades => (GetParent() as MapWorld)?.ActiveBouncers ?? _standaloneGrenades;

    /// <summary>True once <see cref="Setup"/> has run.</summary>
    public bool Initialized { get; private set; }

    public MapPlayer()
    {
        Combat.HealthChanged += OnCombatHealthChanged;
        Combat.WeaponChanged += OnCombatWeaponChanged;
        Combat.Respawn();
    }

    private void OnCombatHealthChanged(int oldHealth, int newHealth)
    {
        EmitSignal(SignalName.HealthChanged, oldHealth, newHealth);
    }

    private void OnCombatWeaponChanged(int gunId)
    {
        EmitSignal(SignalName.WeaponChanged, gunId);
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
        Setup(new OctaPhysics(octa), spawnFeet, yawDegrees);
    }

    /// <summary>
    /// Initialise against an existing world physics instance and place at spawn point.
    /// </summary>
    public void Setup(OctaPhysics phys, Vector3 spawnFeet, float yawDegrees)
    {
        _phys = phys;
        SpawnFeet = spawnFeet;
        SpawnYaw = yawDegrees;
        Entity.Type = Phys.EntPlayer;
        Entity.O = spawnFeet with { Z = spawnFeet.Z + EyeHeight };
        Entity.Yaw = yawDegrees;
        Pitch = 0f;
        Initialized = true;
        _phys.DynEnts.Add(Entity);
        Combat.Spawn(Mode);
        SelectWeapon(Combat.GunSelect >= Guns.First && Combat.GunSelect <= Guns.Last ? (Gun)Combat.GunSelect : DefaultGun);
        SyncTransform();
        if (GetNodeOrNull<Camera3D>("Camera3D") is { } cam) ApplyFov(cam);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationParented)
        {
            if (GetParent() is MapWorld mw && mw.Mode != null)
            {
                Mode = mw.Mode;
            }
        }
    }

    public override void _EnterTree()
    {
        if (GetParent() is MapWorld mw)
        {
            mw.RegisterPlayer(this);
        }
    }

    public override void _ExitTree()
    {
        _phys?.DynEnts.Remove(Entity);
        if (GetParent() is MapWorld mw)
        {
            mw.UnregisterPlayer(this);
        }
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
                if (Floating)
                {
                    // In edit / spectator mode, weapon attacks and wheel cycling are bypassed so editor tools take input.
                    return;
                }

                if (mb.ButtonIndex == MouseButton.Left)
                {
                    if (mb.Pressed && Entity.Dead)
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
            if (Floating)
            {
                // In edit / spectator mode, numeric weapon slots are bypassed so editor tools take input.
                return;
            }

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

    /// <summary>
    /// Gathers input and applies mouse look prior to stepping physics.
    /// Called either from <see cref="_PhysicsProcess"/> when <see cref="AutoAdvance"/> is true,
    /// or from <see cref="MapWorld._PhysicsProcess"/> when centrally managed.
    /// </summary>
    public void PrePhysicsStep()
    {
        // Mouse: the whole frame's accumulated delta is applied once, before stepping
        // (rendergl.cpp mousemove). Every step this frame then sees the same yaw/pitch.
        ApplyMouse();

        // Movement keys (physics.cpp dir()): forward/back -> move, left/right -> strafe.
        // NOTE: strafe is negated relative to dir() because the same Godot/macOS
        // screen-X handedness that flips mouse yaw (see ApplyMouse) also flips the
        // engine's strafe sign, so A/D would otherwise be swapped.
        if (!Entity.Dead)
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
    }

    /// <summary>
    /// Checks attack cooldowns after stepping physics.
    /// </summary>
    public void PostPhysicsStep()
    {
        if (Combat.Attacking && IsAlive)
        {
            if (LastMillis - Combat.LastAction >= Combat.GunWait)
                TryAttack();
        }
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
        if (Entity.Dead)
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
    /// swaps third-person view weapon, switches the first-person hudgun, and emits
    /// <see cref="SignalName.WeaponChanged"/>.
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
        if (!IsAlive) return;
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
        int now = nowMillis ?? LastMillis;
        if (!IsAlive)
        {
            // If dead, pressing attack triggers respawn (fps.cpp:376)
            if (Entity.Dead)
                TryRespawn(now);
            return false;
        }

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
                int targetId = (hit.Target as MapPlayer)?.PlayerId ?? -1;
                EmitSignal(SignalName.DamageDealt, targetId, hit.Damage);
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
                    Textures = (GetParent() as MapWorld)?.Textures,
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

                GetParent()?.AddChild(grenade);
                ActiveGrenades.Add(grenade);
            }
        }

        UpdateBodyAnim();
        UpdateHudgunAnim();
        return true;
    }

    /// <summary>
    /// Handles detonation effects and hit confirmation for a rocket fired by this player
    /// (<c>fpsgame/weapon.cpp:428 explode</c>).
    /// </summary>
    public void OnRocketDetonated(
        RocketProjectile rocket,
        Vector3 impactPos,
        IExplosionTarget? directVictim,
        ExplosionResult<IExplosionTarget> splashResult)
    {
        if (!IsInstanceValid(this)) return;
        if (directVictim != null)
        {
            int qdam = rocket.Gun.Damage * (rocket.QuadDamage ? 4 : 1);
            int directId = (directVictim as MapPlayer)?.PlayerId ?? -1;
            EmitSignal(SignalName.DamageDealt, directId, qdam);
        }
        for (int i = 0; i < splashResult.Hits.Count; i++)
        {
            var hit = splashResult.Hits[i];
            int splashId = (hit.Target as MapPlayer)?.PlayerId ?? -1;
            EmitSignal(SignalName.DamageDealt, splashId, hit.Damage);
        }
        if (splashResult.Hits.Count > 0 || directVictim != null)
        {
            LastHit = LastMillis;
            PlaySound(GameSound.Hit);
        }
        PlaySound(GameSound.RlHit);
        float expRad = rocket.Gun.ExpRad;
        WeaponEffects?.AddExplosionEffect(Gun.RocketLauncher, impactPos, expRad);
        WeaponEffects?.AddScorchMark(impactPos, -rocket.Dir, expRad * 0.5f);
    }

    /// <summary>
    /// Handles detonation effects and hit confirmation for a grenade fired by this player
    /// (<c>fpsgame/weapon.cpp:428 explode</c>).
    /// </summary>
    public void OnGrenadeDetonated(
        BounceBody grenade,
        Vector3 impactPos,
        ExplosionResult<IExplosionTarget> splashResult)
    {
        if (!IsInstanceValid(this)) return;
        for (int i = 0; i < splashResult.Hits.Count; i++)
        {
            var hit = splashResult.Hits[i];
            int splashId = (hit.Target as MapPlayer)?.PlayerId ?? -1;
            EmitSignal(SignalName.DamageDealt, splashId, hit.Damage);
        }
        if (splashResult.Hits.Count > 0)
        {
            LastHit = LastMillis;
            PlaySound(GameSound.Hit);
        }
        PlaySound(GameSound.FExplode);
        float expRad = (grenade.GunInfo ?? WeaponCatalog.Get(Gun.GrenadeLauncher)).ExpRad;
        WeaponEffects?.AddExplosionEffect(Gun.GrenadeLauncher, impactPos, expRad);
        WeaponEffects?.AddScorchMark(impactPos, new Vector3(0f, 0f, 1f), expRad * 0.5f);
    }

    /// <summary>
    /// Apply an item pickup to the player's combat state, emitting <see cref="SignalName.HealthChanged"/>
    /// if health was modified (<c>fpsgame/game.h:412 pickup</c>).
    /// </summary>
    public void Pickup(int itemType)
    {
        int oldHealth = Combat.Health;
        Combat.Pickup(itemType);
    }

    /// <summary>Apply damage to the player, updating health and lastpain timestamp.</summary>
    public void TakeDamage(int damage, int? nowMillis = null)
    {
        if (Entity.Dead) return;

        int now = nowMillis ?? LastMillis;
        Combat.LastPain = now;

        int ad = (damage * ((int)Combat.ArmourType + 1) * 25) / 100;
        if (ad > Combat.Armour) ad = Combat.Armour;
        int healthDamage = damage - ad;

        if (Combat.Health - healthDamage <= 0)
        {
            Combat.Armour -= ad;
            Combat.Health = 0;
            Die(now);
        }
        else
        {
            Combat.DoDamage(damage);
            UpdateBodyAnim();
        }
    }

    /// <summary>Kill the player (engine <c>deathstate</c>, fpsgame/fps.cpp:425).</summary>
    public void Die(int? nowMillis = null)
    {
        int now = nowMillis ?? LastMillis;
        Health = 0;
        Combat.LastPain = now;
        Combat.Attacking = false;
        Entity.State = PhysEnt.CsDead;
        Entity.Move = 0;
        Entity.Strafe = 0;
        Entity.Jumping = false;

        PlaySound(Random.Shared.Next(2) == 0 ? GameSound.Die1 : GameSound.Die2);
        UpdateBodyAnim();

        if (Flags != null)
        {
            Flags.OnPlayerDied(PlayerId, null, false, Entity.FeetPos(), now, Entity.Yaw);
        }

        EmitSignal(SignalName.Died);
    }

    /// <summary>
    /// Attempt to respawn after death if the required minimum delay has elapsed
    /// (<c>fpsgame/fps.cpp:354</c>).
    /// </summary>
    public bool TryRespawn(int? nowMillis = null)
    {
        if (!Entity.Dead) return false;

        int now = nowMillis ?? LastMillis;
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
        Entity.Blocked = false;

        Combat.Spawn(Mode);
        View.ResetAnimState();
        SelectWeapon(Combat.GunSelect >= Guns.First && Combat.GunSelect <= Guns.Last ? (Gun)Combat.GunSelect : DefaultGun);
        SyncTransform();
        UpdateBodyAnim();
        UpdateHudgunAnim();
        EmitSignal(SignalName.Respawned);
    }

    /// <summary>
    /// Executes a single discrete physics simulation step of <paramref name="stepMillis"/> milliseconds.
    /// Input is taken from <see cref="Entity"/> — set it first (or call <see cref="PrePhysicsStep"/>).
    /// Advances player movement, pickups, teleports, rockets, and grenades.
    /// </summary>
    public void Step(int stepMillis, IReadOnlyList<IExplosionTarget>? candidates = null)
    {
        if (!Initialized || _phys == null) return;
        bool wasDead = Entity.Dead;
        _phys.MovePlayer(Entity, Phys.MoveRes, stepMillis);
        // The world advances the shared clock once per substep; only a standalone
        // player owns and advances its private clock.
        if (Clock == null) _standaloneClock.Advance(stepMillis);

        if (!wasDead && Entity.Dead)
        {
            Die(LastMillis);
        }

        if (Combat.CheckQuad(stepMillis))
        {
            PlaySound(GameSound.PupOut);
        }

        Items?.CheckPickups(this, LastMillis);
        CheckJumpPads(LastMillis);
        CheckTeleports(LastMillis);
        CheckFlags(LastMillis);

        var explosionCandidates = candidates ?? new IExplosionTarget[] { this };

        // When running standalone (not under MapWorld), advance own active projectiles & bouncers.
        // Under MapWorld, UpdateProjectiles and UpdateBouncers are called centrally once per substep.
        if (GetParent() is not MapWorld)
        {
            for (int i = _standaloneRockets.Count - 1; i >= 0; i--)
            {
                var rocket = _standaloneRockets[i];
                if (rocket.Advance(stepMillis, _phys.Octa, explosionCandidates))
                {
                    _standaloneRockets.RemoveAt(i);
                }
            }

            for (int i = _standaloneGrenades.Count - 1; i >= 0; i--)
            {
                var grenade = _standaloneGrenades[i];
                if (grenade.Exploded)
                {
                    _standaloneGrenades.RemoveAt(i);
                }
                else
                {
                    grenade.Step(stepMillis, explosionCandidates);
                    if (grenade.Exploded)
                    {
                        _standaloneGrenades.RemoveAt(i);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Advance the simulation by <paramref name="delta"/> seconds in discrete 5 ms steps.
    /// Input is taken from <see cref="Entity"/> — set it first.
    /// Intended for isolated testing; active gameplay players are stepped centrally by MapWorld.
    /// </summary>
    public void Advance(double delta, IReadOnlyList<IExplosionTarget>? targets = null)
    {
        if (!Initialized || _phys == null) return;
        int steps = System.Math.Max(1, (int)System.Math.Round(delta * 1000.0 / Phys.PhysFrameTime));
        var candidates = targets ?? new IExplosionTarget[] { this };
        for (int i = 0; i < steps; i++)
        {
            Step(Phys.PhysFrameTime, candidates);
        }
    }

    /// <summary>Whether the player is in coop-edit mode (engine CS_EDITING).</summary>
    [Export]
    public bool EditMode
    {
        get => Entity.State == PhysEnt.CsEditing;
        set
        {
            Entity.State = value ? PhysEnt.CsEditing : PhysEnt.CsAlive;
            if (value) Entity.Jumping = false;
        }
    }

    /// <summary>Toggles between alive and coop-edit mode.</summary>
    public void ToggleEditMode()
    {
        EditMode = !EditMode;
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

    /// <summary>Places the nested Camera3D for first-person vs third-person view.</summary>
    public void SyncCamera() => View.SyncCamera();

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

    /// <summary>Immediately positions the body model and synchronizes the camera.</summary>
    public void SyncBodyModelNow() => View.SyncBodyModelNow();

    /// <summary>Updates the player node's 3D position and orientation from physics.</summary>
    public void SyncTransform() => View.SyncTransform();

    /// <summary>Places the visible PlayerModel at the player's feet upright and facing yaw + 180°.</summary>
    public void SyncBodyModel() => View.SyncBodyModel();

    /// <summary>Updates body model animation based on current player state.</summary>
    public void UpdateBodyAnim() => View.UpdateBodyAnim();

    /// <summary>Updates first-person hudgun animation.</summary>
    public void UpdateHudgunAnim() => View.UpdateHudgunAnim();

    public override void _Process(double delta)
    {
        View.SyncTransform();
        View.UpdateBodyAnim();
        View.UpdateHudgunAnim();
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
    public virtual void PlaySound(GameSound sound)
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

    /// <summary>
    /// Checks proximity to flags for pickup, steal, return, or score
    /// (matching engine <c>fpsgame/ctf.h:980-1014: checkitems</c>).
    /// </summary>
    public void CheckFlags(int lastMillis)
    {
        if (Flags == null || Mode == null || !IsAlive) return;

        bool isHold = Mode.Has(ModeFlags.Hold);
        Vector3 o = Entity.FeetPos();
        bool tookFlag = false;

        // Pass 1: Enemy flags (in CTF/Protect) or neutral flags (in Hold)
        for (int i = 0; i < Flags.Flags.Count; i++)
        {
            var f = Flags.Flags[i];
            if ((isHold ? f.SpawnIndex < 0 : f.Team is < 1 or > 2 || f.Team == Team) ||
                f.Owner.HasValue ||
                (f.DropTime > 0 && f.DropLoc.X < 0))
            {
                continue;
            }

            Vector3 loc = f.DropTime > 0 ? f.DropLoc : f.SpawnLoc;
            if (o.DistanceTo(loc) < Game.FlagManager.FlagRadius)
            {
                if ((Combat.FlagPickup & (1 << f.Id)) != 0) continue;

                tookFlag = true;
                Flags.TakeFlag(PlayerId, Team, o, f.Id, f.Version, lastMillis);
                Combat.FlagPickup |= (1 << f.Id);
            }
            else
            {
                Combat.FlagPickup &= ~(1 << f.Id);
            }
        }

        if (isHold) return;

        // Pass 2: Friendly flags (return dropped friendly flag, or touch base to capture carried enemy flag)
        for (int i = 0; i < Flags.Flags.Count; i++)
        {
            var f = Flags.Flags[i];
            if (f.Team is < 1 or > 2 || f.Team != Team || f.Owner.HasValue || (f.DropTime > 0 && f.DropLoc.X < 0))
            {
                continue;
            }

            Vector3 loc = f.DropTime > 0 ? f.DropLoc : f.SpawnLoc;
            if (o.DistanceTo(loc) < Game.FlagManager.FlagRadius)
            {
                if (!tookFlag && (Combat.FlagPickup & (1 << f.Id)) != 0) continue;

                Flags.TakeFlag(PlayerId, Team, o, f.Id, f.Version, lastMillis);
                Combat.FlagPickup |= (1 << f.Id);
            }
            else
            {
                Combat.FlagPickup &= ~(1 << f.Id);
            }
        }
    }
}
