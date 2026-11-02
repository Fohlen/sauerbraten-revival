using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using SauerbratenRevival.World.Content;
using SauerbratenRevival.World.Game;

namespace SauerbratenRevival;

/// <summary>
/// Map loading screen and stepped progress bar controller (ADR 0011 §5).
/// Backs <c>scenes/map_load.tscn</c>.
/// </summary>
public partial class MapLoad : CanvasLayer
{
    [Export] public TextureRect? MapshotRect;
    [Export] public Label? TitleLabel;
    [Export] public Label? InfoLabel;
    [Export] public Label? StatusLabel;
    [Export] public ProgressBar? ProgressBar;

    public float CurrentProgress { get; private set; }
    public string CurrentStatus { get; private set; } = "";
    public string MapName { get; private set; } = "";
    public string GameMode { get; private set; } = "";

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        ResolveNodes();
    }

    private void ResolveNodes()
    {
        MapshotRect ??= GetNodeOrNull<TextureRect>("%Mapshot")
            ?? GetNodeOrNull<TextureRect>("CenterContainer/Content/MapshotFrame/Mapshot");
        TitleLabel ??= GetNodeOrNull<Label>("%TitleLabel")
            ?? GetNodeOrNull<Label>("CenterContainer/Content/HeaderBox/TitleLabel");
        InfoLabel ??= GetNodeOrNull<Label>("%InfoLabel")
            ?? GetNodeOrNull<Label>("CenterContainer/Content/HeaderBox/InfoLabel");
        StatusLabel ??= GetNodeOrNull<Label>("%StatusLabel")
            ?? GetNodeOrNull<Label>("CenterContainer/Content/ProgressBox/StatusLabel");
        ProgressBar ??= GetNodeOrNull<ProgressBar>("%ProgressBar")
            ?? GetNodeOrNull<ProgressBar>("CenterContainer/Content/ProgressBox/ProgressBar");
    }

    /// <summary>
    /// Displays the loading screen and configures map metadata and screenshot backdrop.
    /// </summary>
    public void StartLoading(string map, string mode, string mapTitle = "", string author = "")
    {
        ResolveNodes();
        MapName = map;
        GameMode = mode;
        CurrentProgress = 0.0f;
        Visible = true;

        string cleanMap = map.Replace(".ogz", "").TrimStart('/');
        string title = !string.IsNullOrWhiteSpace(mapTitle) ? mapTitle : cleanMap.GetFile().GetBaseName();
        if (TitleLabel != null) TitleLabel.Text = title.ToUpperInvariant();

        var modeInfo = GameModeCatalog.TryGet(mode);
        string modeText = modeInfo?.Name ?? mode.ToUpperInvariant();
        if (InfoLabel != null)
        {
            InfoLabel.Text = !string.IsNullOrWhiteSpace(author)
                ? $"{modeText}  •  Author: {author}"
                : $"{modeText}  •  {cleanMap}";
        }

        var mapshot = ResolveMapshot(cleanMap);
        if (MapshotRect != null)
        {
            MapshotRect.Texture = mapshot;
            MapshotRect.Visible = mapshot != null;
        }

        ApplyProgress(0.0f, "Initializing Map...");
    }

    /// <summary>
    /// Advances progress by a relative increment, updates status, and yields one frame
    /// to Godot's process loop so the UI renders. Matches Func&lt;float, string, Task&gt;.
    /// </summary>
    public async Task StepProgressAsync(float increment, string status)
    {
        ApplyProgress(CurrentProgress + increment, status);
        if (IsInsideTree())
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    /// <summary>
    /// Advances progress by +20% and yields one frame. Matches Func&lt;string, Task&gt;.
    /// </summary>
    public Task StepProgressAsync(string status) => StepProgressAsync(0.20f, status);

    private void ApplyProgress(float progress, string status)
    {
        CurrentProgress = Mathf.Clamp(progress, 0.0f, 1.0f);
        CurrentStatus = status;

        if (ProgressBar != null)
        {
            ProgressBar.Value = CurrentProgress * 100.0;
        }
        if (StatusLabel != null)
        {
            int percent = Mathf.RoundToInt(CurrentProgress * 100.0f);
            StatusLabel.Text = $"{status} ({percent}%)";
        }
    }

    /// <summary>
    /// Hides the loading screen and resets visibility.
    /// </summary>
    public void Dismiss()
    {
        Visible = false;
    }

    /// <summary>
    /// Resolve map preview screenshot from assets, falling back to disk or reference tree.
    /// </summary>
    public static Texture2D? ResolveMapshot(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName)) return null;

        string clean = mapName.Replace(".ogz", "").TrimStart('/');
        if (clean.StartsWith("res://")) clean = clean.Substring("res://".Length);
        if (clean.StartsWith("assets/packages/")) clean = clean.Substring("assets/packages/".Length);

        string relative = clean.StartsWith("base/") ? $"{clean}.jpg" : $"base/{clean}.jpg";
        string resPath = ContentRoots.ProjectPath(ContentKind.Map, relative);
        if (ResourceLoader.Exists(resPath)) return ResourceLoader.Load<Texture2D>(resPath);

        string? diskPath = ContentRoots.TryResolve(ContentKind.Map, relative);
        if (diskPath == null) return null;
        var image = new Image();
        return image.Load(diskPath) == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
    }
}
