namespace SauerbratenRevival.World.Entities;

/// <summary>
/// One playable model's variants (render.cpp <c>playermodelinfo</c>). The engine
/// keeps a fixed table of five selectable player models; each names its FFA body,
/// the blue/red team skins, its first-person <c>hudguns</c>, an optional body
/// view-weapon (<c>vwep</c>), quad/powerup attachment and the three armour skins.
/// </summary>
public sealed class PlayermodelInfo
{
    public required string Ffa { get; init; }
    public required string BlueTeam { get; init; }
    public required string RedTeam { get; init; }
    public required string Hudguns { get; init; }
    public string? Vwep { get; init; }
    public string? Quad { get; init; }
    public required string?[] Armour { get; init; }
    public required string FfaIcon { get; init; }
    public required string BlueIcon { get; init; }
    public required string RedIcon { get; init; }
    public required bool Ragdoll { get; init; }

    /// <summary>The body model for a team (0 = FFA, 1 = blue, 2 = red).</summary>
    public string Body(int team) => team switch
    {
        1 => BlueTeam,
        2 => RedTeam,
        _ => Ffa,
    };
}

/// <summary>
/// The engine's fixed player-model table (render.cpp <c>playermodels[5]</c>). These
/// are the models a client can select; all are skeletal MD5 (see the model
/// importer). Index is the <c>playermodel</c> variable (0..4).
/// </summary>
public static class Playermodels
{
    /// <summary>render.cpp <c>playermodels[5]</c>.</summary>
    public static readonly PlayermodelInfo[] All =
    {
        new()
        {
            Ffa = "mrfixit", BlueTeam = "mrfixit/blue", RedTeam = "mrfixit/red",
            Hudguns = "mrfixit/hudguns", Vwep = null, Quad = "mrfixit/horns",
            Armour = new[] { "mrfixit/armor/blue", "mrfixit/armor/green", "mrfixit/armor/yellow" },
            FfaIcon = "mrfixit", BlueIcon = "mrfixit_blue", RedIcon = "mrfixit_red", Ragdoll = true,
        },
        new()
        {
            Ffa = "snoutx10k", BlueTeam = "snoutx10k/blue", RedTeam = "snoutx10k/red",
            Hudguns = "snoutx10k/hudguns", Vwep = null, Quad = "snoutx10k/wings",
            Armour = new[] { "snoutx10k/armor/blue", "snoutx10k/armor/green", "snoutx10k/armor/yellow" },
            FfaIcon = "snoutx10k", BlueIcon = "snoutx10k_blue", RedIcon = "snoutx10k_red", Ragdoll = true,
        },
        // The engine's third entry (ogro/green) is commented out; ogro2 is index 2.
        new()
        {
            Ffa = "ogro2", BlueTeam = "ogro2/blue", RedTeam = "ogro2/red",
            Hudguns = "mrfixit/hudguns", Vwep = null, Quad = "ogro2/quad",
            Armour = new[] { "ogro2/armor/blue", "ogro2/armor/green", "ogro2/armor/yellow" },
            FfaIcon = "ogro", BlueIcon = "ogro_blue", RedIcon = "ogro_red", Ragdoll = true,
        },
        new()
        {
            Ffa = "inky", BlueTeam = "inky/blue", RedTeam = "inky/red",
            Hudguns = "inky/hudguns", Vwep = null, Quad = "inky/quad",
            Armour = new[] { "inky/armor/blue", "inky/armor/green", "inky/armor/yellow" },
            FfaIcon = "inky", BlueIcon = "inky_blue", RedIcon = "inky_red", Ragdoll = true,
        },
        new()
        {
            Ffa = "captaincannon", BlueTeam = "captaincannon/blue", RedTeam = "captaincannon/red",
            Hudguns = "captaincannon/hudguns", Vwep = null, Quad = "captaincannon/quad",
            Armour = new[] { "captaincannon/armor/blue", "captaincannon/armor/green", "captaincannon/armor/yellow" },
            FfaIcon = "captaincannon", BlueIcon = "captaincannon_blue", RedIcon = "captaincannon_red", Ragdoll = true,
        },
    };

    /// <summary>Clamped lookup (<c>getplayermodelinfo(int)</c>).</summary>
    public static PlayermodelInfo Get(int index) => All[System.Math.Clamp(index, 0, All.Length - 1)];

    /// <summary>render.cpp <c>chooserandomplayermodel</c>.</summary>
    public static int ChooseRandom(int seed) => (seed & 0xFFFF) % All.Length;
}
