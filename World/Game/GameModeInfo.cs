using System;
using System.Numerics;
using Godot;
using SauerbratenRevival.World.Game.Items;
using SauerbratenRevival.World.Game.Objectives;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// A composable game mode definition binding an objective, mutator, loadout, pickup rules,
/// and combat parameters. Authored as a Godot <see cref="Resource"/> (<c>.tres</c>) and
/// pre-baked for O(1) zero-allocation reads on the 200 Hz simulation hot path.
/// </summary>
[Tool]
[GlobalClass]
public partial class GameModeInfo : Resource
{
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

    // --- Spawn Loadout ---
    [Export] public int SpawnHealth { get; set; } = 100;
    [Export] public int MaxHealth { get; set; } = 100;
    [Export] public int SpawnArmour { get; set; } = 25;
    [Export] public ArmourType SpawnArmourType { get; set; } = ArmourType.Blue;
    [Export] public Gun SpawnGun { get; set; } = Gun.Pistol;
    [Export] public int[] SpawnAmmo { get; set; } = { 1, 0, 0, 0, 0, 1, 40 };
    [Export] public int RandomGunsCount { get; set; } = 0;
    [Export] public int RandomGunsAmmoScale { get; set; } = 1;

    // --- World Pickups ---
    [Export] public ItemSpawnMask AllowedItems { get; set; } = ItemSpawnMask.All;
    [Export] public float ItemRespawnScale { get; set; } = 1.0f;

    // --- Combat & Match Lifecycle ---
    [Export] public bool InfiniteAmmo { get; set; } = false;
    [Export] public float DamageScale { get; set; } = 1.0f;
    [Export] public float SelfDamageScale { get; set; } = 0.5f;
    [Export] public float SelfPushScale { get; set; } = 2.5f;
    [Export] public float HitPushScale { get; set; } = 1.0f;
    [Export] public float FriendlyFireScale { get; set; } = 0.0f;
    [Export] public int RespawnDelayMillis { get; set; } = 500;
    [Export] public int ScoreLimit { get; set; } = 0;
    [Export] public int MatchDurationSeconds { get; set; } = 600;

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
    /// Flattens mutators and objective defaults onto this mode definition and precomputes
    /// the weapon pool mask for zero-allocation hot paths.
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

        if (Objective != null)
        {
            if (SpawnArmour == 25 && Objective.DefaultSpawnArmour != 25 && string.IsNullOrEmpty(Mutator))
            {
                SpawnArmour = Objective.DefaultSpawnArmour;
            }
        }

        // 2. Apply Mutator if specified
        if (!string.IsNullOrEmpty(Mutator))
        {
            var mut = GameMutatorCatalog.TryGet(Mutator);
            if (mut != null)
            {
                SpawnHealth = mut.SpawnHealth;
                MaxHealth = mut.MaxHealth;
                SpawnArmour = mut.SpawnArmour;
                SpawnArmourType = mut.SpawnArmourType;
                SpawnGun = mut.SpawnGun;
                SpawnAmmo = (int[])mut.SpawnAmmo.Clone();
                RandomGunsCount = mut.RandomGunsCount;
                RandomGunsAmmoScale = mut.RandomGunsAmmoScale;
                AllowedItems = mut.AllowedItems;
                ItemRespawnScale = mut.ItemRespawnScale;
                InfiniteAmmo = mut.InfiniteAmmo;
                DamageScale = mut.DamageScale;
                SelfDamageScale = mut.SelfDamageScale;
                SelfPushScale = mut.SelfPushScale;
                HitPushScale = mut.HitPushScale;
                FriendlyFireScale = mut.FriendlyFireScale;
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
        SpawnHealth = mut.SpawnHealth;
        MaxHealth = mut.MaxHealth;
        SpawnArmour = mut.SpawnArmour;
        SpawnArmourType = mut.SpawnArmourType;
        SpawnGun = mut.SpawnGun;
        SpawnAmmo = (int[])mut.SpawnAmmo.Clone();
        RandomGunsCount = mut.RandomGunsCount;
        RandomGunsAmmoScale = mut.RandomGunsAmmoScale;
        AllowedItems = mut.AllowedItems;
        ItemRespawnScale = mut.ItemRespawnScale;
        InfiniteAmmo = mut.InfiniteAmmo;
        DamageScale = mut.DamageScale;
        SelfDamageScale = mut.SelfDamageScale;
        SelfPushScale = mut.SelfPushScale;
        HitPushScale = mut.HitPushScale;
        FriendlyFireScale = mut.FriendlyFireScale;
    }

    public override string ToString() => string.IsNullOrEmpty(Id) ? base.ToString() : Id;
}
