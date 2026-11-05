using System;
using System.Numerics;
using Godot;
using SauerbratenRevival.World.Game.Items;
using SauerbratenRevival.World.Game.Objectives;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// Bitmask tracking which loadout and combat properties have been explicitly overridden
/// at the game mode level, preventing mutator presets from overwriting them in <see cref="GameModeInfo.Bake"/>.
/// </summary>
[Flags]
public enum ModeOverrideMask : uint
{
    None = 0,
    SpawnHealth = 1 << 0,
    MaxHealth = 1 << 1,
    SpawnArmour = 1 << 2,
    SpawnArmourType = 1 << 3,
    SpawnGun = 1 << 4,
    SpawnAmmo = 1 << 5,
    RandomGunsCount = 1 << 6,
    RandomGunsAmmoScale = 1 << 7,
    AllowedItems = 1 << 8,
    ItemRespawnScale = 1 << 9,
    InfiniteAmmo = 1 << 10,
    DamageScale = 1 << 11,
    SelfDamageScale = 1 << 12,
    SelfPushScale = 1 << 13,
    HitPushScale = 1 << 14,
    FriendlyFireScale = 1 << 15,
    All = 0xFFFF,
}

/// <summary>
/// A composable game mode definition binding an objective, mutator, loadout, pickup rules,
/// and combat parameters. Authored as a Godot <see cref="Resource"/> (<c>.tres</c>) and
/// pre-baked for O(1) zero-allocation reads on the 200 Hz simulation hot path.
/// </summary>
[Tool]
[GlobalClass]
public partial class GameModeInfo : Resource
{
    private readonly bool _initialized;
    private ModeOverrideMask _overrides = ModeOverrideMask.None;

    public GameModeInfo()
    {
        _initialized = true;
    }

    /// <summary>Stable, filename-safe key (e.g. <c>insta_ctf</c>); also the <c>.tres</c> file stem.</summary>
    [Export] public string Id { get; set; } = "";

    /// <summary>Human-readable / engine name (e.g. "insta ctf").</summary>
    [Export] public string Name { get; set; } = "";

    /// <summary>Objective ID resolved via <see cref="ObjectiveCatalog"/> (e.g. "dm", "edit", "ctf").</summary>
    [Export] public string ObjectiveId { get; set; } = "dm";

    /// <summary>Optional mutator preset ID resolved via <see cref="GameMutatorCatalog"/> (e.g. "insta", "effic", "tactics").</summary>
    [Export] public string Mutator { get; set; } = "";

    /// <summary>Whether this mode splits players into Blue (team 1) and Red (team 2).</summary>
    [Export] public bool TeamPlay { get; set; } = false;

    /// <summary>
    /// Explicit mask of loadout and combat properties overridden at the mode level.
    /// Setting an overridable property automatically sets the corresponding bit here.
    /// During <see cref="Bake"/>, mutator presets only fill fields that are not flagged.
    /// </summary>
    [Export]
    public ModeOverrideMask Overrides
    {
        get => _overrides;
        set => _overrides = value;
    }

    // --- Spawn Loadout ---
    private int _spawnHealth = 100;
    [Export]
    public int SpawnHealth
    {
        get => _spawnHealth;
        set
        {
            _spawnHealth = value;
            if (_initialized) _overrides |= ModeOverrideMask.SpawnHealth;
        }
    }

    private int _maxHealth = 100;
    [Export]
    public int MaxHealth
    {
        get => _maxHealth;
        set
        {
            _maxHealth = value;
            if (_initialized) _overrides |= ModeOverrideMask.MaxHealth;
        }
    }

    private int _spawnArmour = 25;
    [Export]
    public int SpawnArmour
    {
        get => _spawnArmour;
        set
        {
            _spawnArmour = value;
            if (_initialized) _overrides |= ModeOverrideMask.SpawnArmour;
        }
    }

    private ArmourType _spawnArmourType = ArmourType.Blue;
    [Export]
    public ArmourType SpawnArmourType
    {
        get => _spawnArmourType;
        set
        {
            _spawnArmourType = value;
            if (_initialized) _overrides |= ModeOverrideMask.SpawnArmourType;
        }
    }

    private Gun _spawnGun = Gun.Pistol;
    [Export]
    public Gun SpawnGun
    {
        get => _spawnGun;
        set
        {
            _spawnGun = value;
            if (_initialized) _overrides |= ModeOverrideMask.SpawnGun;
        }
    }

    private int[] _spawnAmmo = { 1, 0, 0, 0, 0, 1, 40 };
    [Export]
    public int[] SpawnAmmo
    {
        get => _spawnAmmo;
        set
        {
            _spawnAmmo = value != null ? (int[])value.Clone() : new[] { 1, 0, 0, 0, 0, 1, 40 };
            if (_initialized) _overrides |= ModeOverrideMask.SpawnAmmo;
        }
    }

    private int _randomGunsCount = 0;
    [Export]
    public int RandomGunsCount
    {
        get => _randomGunsCount;
        set
        {
            _randomGunsCount = value;
            if (_initialized) _overrides |= ModeOverrideMask.RandomGunsCount;
        }
    }

    private int _randomGunsAmmoScale = 1;
    [Export]
    public int RandomGunsAmmoScale
    {
        get => _randomGunsAmmoScale;
        set
        {
            _randomGunsAmmoScale = value;
            if (_initialized) _overrides |= ModeOverrideMask.RandomGunsAmmoScale;
        }
    }

    // --- World Pickups ---
    private ItemSpawnMask _allowedItems = ItemSpawnMask.All;
    [Export]
    public ItemSpawnMask AllowedItems
    {
        get => _allowedItems;
        set
        {
            _allowedItems = value;
            if (_initialized) _overrides |= ModeOverrideMask.AllowedItems;
        }
    }

    private float _itemRespawnScale = 1.0f;
    [Export]
    public float ItemRespawnScale
    {
        get => _itemRespawnScale;
        set
        {
            _itemRespawnScale = value;
            if (_initialized) _overrides |= ModeOverrideMask.ItemRespawnScale;
        }
    }

    // --- Combat & Match Lifecycle ---
    private bool _infiniteAmmo = false;
    [Export]
    public bool InfiniteAmmo
    {
        get => _infiniteAmmo;
        set
        {
            _infiniteAmmo = value;
            if (_initialized) _overrides |= ModeOverrideMask.InfiniteAmmo;
        }
    }

    private float _damageScale = 1.0f;
    [Export]
    public float DamageScale
    {
        get => _damageScale;
        set
        {
            _damageScale = value;
            if (_initialized) _overrides |= ModeOverrideMask.DamageScale;
        }
    }

    private float _selfDamageScale = 0.5f;
    [Export]
    public float SelfDamageScale
    {
        get => _selfDamageScale;
        set
        {
            _selfDamageScale = value;
            if (_initialized) _overrides |= ModeOverrideMask.SelfDamageScale;
        }
    }

    private float _selfPushScale = 2.5f;
    [Export]
    public float SelfPushScale
    {
        get => _selfPushScale;
        set
        {
            _selfPushScale = value;
            if (_initialized) _overrides |= ModeOverrideMask.SelfPushScale;
        }
    }

    private float _hitPushScale = 1.0f;
    [Export]
    public float HitPushScale
    {
        get => _hitPushScale;
        set
        {
            _hitPushScale = value;
            if (_initialized) _overrides |= ModeOverrideMask.HitPushScale;
        }
    }

    private float _friendlyFireScale = 0.0f;
    [Export]
    public float FriendlyFireScale
    {
        get => _friendlyFireScale;
        set
        {
            _friendlyFireScale = value;
            if (_initialized) _overrides |= ModeOverrideMask.FriendlyFireScale;
        }
    }

    [Export] public int RespawnDelayMillis { get; set; } = 500;
    [Export] public int ScoreLimit { get; set; } = 0;
    [Export] public int MatchDurationSeconds { get; set; } = 600;

    /// <summary>Returns true if the specified field mask has been explicitly overridden on this mode.</summary>
    public bool HasOverride(ModeOverrideMask mask) => (_overrides & mask) != 0;

    /// <summary>Clears all explicit mode-level overrides, restoring full mutator and objective inheritance.</summary>
    public void ClearOverrides() => _overrides = ModeOverrideMask.None;

    /// <summary>Sets starting ammo for a specific gun and flags <see cref="ModeOverrideMask.SpawnAmmo"/>.</summary>
    public void SetSpawnAmmo(Gun gun, int amount)
    {
        if ((int)gun >= 0 && (int)gun < _spawnAmmo.Length)
        {
            _spawnAmmo[(int)gun] = amount;
            if (_initialized) _overrides |= ModeOverrideMask.SpawnAmmo;
        }
    }

    // --- Pre-Baked Runtime Properties (Computed in Bake, O(1) Hot Path) ---

    /// <summary>Resolved objective resource definition.</summary>
    public ModeObjectiveResource Objective { get; set; } = null!;

    /// <summary>Bitmask (1 &lt;&lt; Gun) of player guns (SG..Pistol) obtainable in this mode.</summary>
    public int WeaponPoolMask { get; private set; } = 0b111_1110;

    /// <summary>Number of player guns (SG..Pistol) in <see cref="WeaponPoolMask"/>.</summary>
    public int WeaponPoolCount => BitOperations.PopCount((uint)WeaponPoolMask);

    /// <summary>True when this mode's objective boots into the interactive world editor.</summary>
    public bool IsEdit => Objective?.IsEditMode ?? (ObjectiveId == "edit");

    /// <summary>Instantiates a fresh runtime objective controller for a match.</summary>
    public IModeObjective CreateObjective() =>
        (Objective ?? ObjectiveCatalog.TryGet(ObjectiveId) ?? ObjectiveCatalog.Get("dm")).CreateInstance(this);

    /// <summary>O(1) bitwise check for whether a map item entity type is allowed to spawn.</summary>
    public bool CanSpawnItem(ItemType type) =>
        type >= ItemType.Shells && type <= ItemType.QuadDamage &&
        (AllowedItems & (ItemSpawnMask)(1 << (int)type)) != 0;

    /// <summary>
    /// Flattens mutators and objective defaults onto this mode definition following the explicit
    /// override merge policy:
    /// 1. Objective baseline defaults (e.g. <see cref="ModeObjectiveResource.DefaultSpawnArmour"/>)
    /// 2. Mutator preset loadout and combat fields (for any field not overridden at mode level)
    /// 3. Mode-level inline overrides (fields marked in <see cref="Overrides"/>)
    /// Finally, precomputes the weapon pool mask for zero-allocation hot paths.
    /// </summary>
    public void Bake()
    {
        // 1. Resolve Objective definition
        if (Objective != null)
        {
            if (string.IsNullOrEmpty(ObjectiveId) || ObjectiveId == "dm")
                ObjectiveId = Objective.Id;
        }
        else
        {
            if (!string.IsNullOrEmpty(ObjectiveId))
                Objective = ObjectiveCatalog.TryGet(ObjectiveId) ?? ObjectiveCatalog.TryGet("dm")!;
            else
                Objective = ObjectiveCatalog.TryGet("dm")!;
        }

        // Apply Objective default starting armour when not overridden and no mutator is used
        if (Objective != null && string.IsNullOrEmpty(Mutator))
        {
            if (!HasOverride(ModeOverrideMask.SpawnArmour))
            {
                _spawnArmour = Objective.DefaultSpawnArmour;
            }
        }

        // 2. Apply Mutator if specified (filling only non-overridden fields)
        if (!string.IsNullOrEmpty(Mutator))
        {
            var mut = GameMutatorCatalog.TryGet(Mutator);
            if (mut != null)
            {
                ApplyMutator(mut);
            }
        }

        // 3. Precompute WeaponPoolMask
        int mask = 0;
        if (SpawnGun >= Gun.Shotgun && SpawnGun <= Gun.Pistol)
            mask |= 1 << (int)SpawnGun;

        for (int g = (int)Gun.Shotgun; g <= (int)Gun.Pistol; g++)
        {
            if (g < SpawnAmmo.Length && SpawnAmmo[g] > 0)
                mask |= 1 << g;
        }

        if (RandomGunsCount > 0)
        {
            for (int g = (int)Gun.Shotgun; g <= (int)Gun.GrenadeLauncher; g++)
                mask |= 1 << g;
        }

        if ((AllowedItems & ItemSpawnMask.Shells) != 0) mask |= 1 << (int)Gun.Shotgun;
        if ((AllowedItems & ItemSpawnMask.Bullets) != 0) mask |= 1 << (int)Gun.Chaingun;
        if ((AllowedItems & ItemSpawnMask.Rockets) != 0) mask |= 1 << (int)Gun.RocketLauncher;
        if ((AllowedItems & ItemSpawnMask.RifleRounds) != 0) mask |= 1 << (int)Gun.Rifle;
        if ((AllowedItems & ItemSpawnMask.Grenades) != 0) mask |= 1 << (int)Gun.GrenadeLauncher;
        if ((AllowedItems & ItemSpawnMask.Cartridges) != 0) mask |= 1 << (int)Gun.Pistol;

        WeaponPoolMask = mask;
    }

    private void ApplyMutator(GameMutator mut)
    {
        if (!HasOverride(ModeOverrideMask.SpawnHealth)) _spawnHealth = mut.SpawnHealth;
        if (!HasOverride(ModeOverrideMask.MaxHealth)) _maxHealth = mut.MaxHealth;
        if (!HasOverride(ModeOverrideMask.SpawnArmour)) _spawnArmour = mut.SpawnArmour;
        if (!HasOverride(ModeOverrideMask.SpawnArmourType)) _spawnArmourType = mut.SpawnArmourType;
        if (!HasOverride(ModeOverrideMask.SpawnGun)) _spawnGun = mut.SpawnGun;
        if (!HasOverride(ModeOverrideMask.SpawnAmmo)) _spawnAmmo = (int[])mut.SpawnAmmo.Clone();
        if (!HasOverride(ModeOverrideMask.RandomGunsCount)) _randomGunsCount = mut.RandomGunsCount;
        if (!HasOverride(ModeOverrideMask.RandomGunsAmmoScale)) _randomGunsAmmoScale = mut.RandomGunsAmmoScale;
        if (!HasOverride(ModeOverrideMask.AllowedItems)) _allowedItems = mut.AllowedItems;
        if (!HasOverride(ModeOverrideMask.ItemRespawnScale)) _itemRespawnScale = mut.ItemRespawnScale;
        if (!HasOverride(ModeOverrideMask.InfiniteAmmo)) _infiniteAmmo = mut.InfiniteAmmo;
        if (!HasOverride(ModeOverrideMask.DamageScale)) _damageScale = mut.DamageScale;
        if (!HasOverride(ModeOverrideMask.SelfDamageScale)) _selfDamageScale = mut.SelfDamageScale;
        if (!HasOverride(ModeOverrideMask.SelfPushScale)) _selfPushScale = mut.SelfPushScale;
        if (!HasOverride(ModeOverrideMask.HitPushScale)) _hitPushScale = mut.HitPushScale;
        if (!HasOverride(ModeOverrideMask.FriendlyFireScale)) _friendlyFireScale = mut.FriendlyFireScale;
    }

    public override string ToString() => string.IsNullOrEmpty(Id) ? base.ToString() : Id;
}
