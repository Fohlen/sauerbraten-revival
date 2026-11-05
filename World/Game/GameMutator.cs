using Godot;
using SauerbratenRevival.World.Game.Items;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game;

[Tool]
[GlobalClass]
public partial class GameMutator : Resource
{
    [Export] public string Id { get; set; } = "";
    [Export] public string Name { get; set; } = "";

    // --- Spawn Loadout ---
    [Export] public int SpawnHealth { get; set; } = 100;
    [Export] public int MaxHealth { get; set; } = 100;
    [Export] public int SpawnArmour { get; set; } = 0;
    [Export] public ArmourType SpawnArmourType { get; set; } = ArmourType.Blue;
    [Export] public Gun SpawnGun { get; set; } = Gun.Pistol;
    [Export] public int[] SpawnAmmo { get; set; } = { 1, 0, 0, 0, 0, 0, 0 };
    [Export] public int RandomGunsCount { get; set; } = 0;
    [Export] public int RandomGunsAmmoScale { get; set; } = 1;

    // --- World Pickups ---
    [Export] public ItemSpawnMask AllowedItems { get; set; } = ItemSpawnMask.None;
    [Export] public float ItemRespawnScale { get; set; } = 1.0f;

    // --- Combat Modifiers ---
    [Export] public bool InfiniteAmmo { get; set; } = false;
    [Export] public float DamageScale { get; set; } = 1.0f;
    [Export] public float SelfDamageScale { get; set; } = 0.5f; // 1 / EXP_SELFDAMDIV
    [Export] public float SelfPushScale { get; set; } = 2.5f;   // EXP_SELFPUSH
    [Export] public float HitPushScale { get; set; } = 1.0f;
    [Export] public float FriendlyFireScale { get; set; } = 0.0f;

    public override string ToString() => string.IsNullOrEmpty(Id) ? base.ToString() : Id;
}
