using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SauerbratenRevival.World.Save;

namespace SauerbratenRevival;

/// <summary>
/// A small single-panel wizard that converts one legacy <c>.ogz</c> map into the
/// native format (docs/ogz-import.md): pick the Sauerbraten install folder, pick a
/// map from a filterable list, convert.
///
/// The install path is remembered in <c>user://settings.cfg</c> so it only has to
/// be chosen once. Converted maps are written under <c>user://maps/&lt;map&gt;/</c>
/// (docs/editor-and-commands.md §7.4: user space is the writable tier).
///
/// The UI is built in code so the scene file stays a one-line stub; this is the
/// project's first GUI and a deliberate exercise in the Control API.
/// </summary>
public partial class ConverterWizard : Control
{
    /// <summary>Config file under <c>user://</c> holding the remembered paths.</summary>
    public const string SettingsPath = "user://settings.cfg";
    public const string SauerbratenPathKey = "sauerbraten/path";

    /// <summary>Project-relative directory holding the committed metadata catalogue.</summary>
    public const string CatalogueDir = "res://metadata-catalogue";

    /// <summary>Where converted maps are written (user space, §7.4).</summary>
    public const string OutputRoot = "user://maps";

    private LineEdit _pathEdit = null!;
    private OptionButton _mapSelect = null!;
    private LineEdit _filter = null!;
    private Button _nextButton = null!;
    private Label _result = null!;

    /// <summary>Every discovered map, as a package-qualified name (e.g. <c>base/church51</c>).</summary>
    private readonly List<string> _maps = new();
    private FileDialog _folderDialog = null!;

    public override void _Ready()
    {
        BuildUi();
        string saved = LoadSetting(SauerbratenPathKey);
        _pathEdit.Text = !string.IsNullOrWhiteSpace(saved) ? saved : World.Ogz.MapRoots.ReferenceRoot;
        RescanMaps();
    }

    // ---- UI ----

    private void BuildUi()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        const int pad = 16;
        margin.AddThemeConstantOverride("margin_left", pad);
        margin.AddThemeConstantOverride("margin_right", pad);
        margin.AddThemeConstantOverride("margin_top", pad);
        margin.AddThemeConstantOverride("margin_bottom", pad);
        AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        margin.AddChild(column);

        var title = new Label { Text = "OGZ → native map converter" };
        title.AddThemeFontSizeOverride("font_size", 20);
        column.AddChild(title);

        // Step 1: Sauerbraten folder.
        column.AddChild(new Label { Text = "1. Sauerbraten install folder" });
        var pathRow = new HBoxContainer();
        pathRow.AddThemeConstantOverride("separation", 8);
        column.AddChild(pathRow);
        _pathEdit = new LineEdit { PlaceholderText = "…/sauerbraten-code-r6889", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _pathEdit.TextSubmitted += _ => RescanMaps();
        pathRow.AddChild(_pathEdit);
        var browse = new Button { Text = "Browse…" };
        browse.Pressed += OnBrowsePressed;
        pathRow.AddChild(browse);

        // Step 2: map select.
        column.AddChild(new Label { Text = "2. Map" });
        _filter = new LineEdit { PlaceholderText = "Filter maps…" };
        _filter.TextChanged += _ => PopulateMapSelect();
        column.AddChild(_filter);
        _mapSelect = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _mapSelect.ItemSelected += _ => _nextButton.Disabled = false;
        column.AddChild(_mapSelect);

        // Step 3: convert.
        _nextButton = new Button { Text = "Next", Disabled = true };
        _nextButton.Pressed += OnNextPressed;
        column.AddChild(_nextButton);

        _result = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        column.AddChild(_result);

        _folderDialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Select the Sauerbraten install folder",
        };
        _folderDialog.DirSelected += OnFolderSelected;
        AddChild(_folderDialog);
    }

    private void OnBrowsePressed()
    {
        string current = ProjectSettings.GlobalizePath(_pathEdit.Text.Trim());
        if (Directory.Exists(current)) _folderDialog.CurrentDir = current;
        _folderDialog.PopupCentered(new Vector2I(800, 600));
    }

    private void OnFolderSelected(string dir)
    {
        _pathEdit.Text = dir;
        SaveSetting(SauerbratenPathKey, dir);
        RescanMaps();
    }

    // ---- map discovery ----

    /// <summary>Rescan <c>&lt;install&gt;/packages/**/*.ogz</c> and repopulate the select.</summary>
    private void RescanMaps()
    {
        _maps.Clear();
        string root = ProjectSettings.GlobalizePath(_pathEdit.Text.Trim());
        string packages = Path.Combine(root, "packages");
        if (Directory.Exists(packages))
        {
            foreach (var file in Directory.EnumerateFiles(packages, "*.ogz", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(packages, file);
                _maps.Add(Path.ChangeExtension(rel, null).Replace('\\', '/'));
            }
            _maps.Sort(System.StringComparer.OrdinalIgnoreCase);
        }
        PopulateMapSelect();
        _result.Text = _maps.Count > 0
            ? $"Found {_maps.Count} map(s)."
            : $"No .ogz files found under '{packages}'.";
    }

    private void PopulateMapSelect()
    {
        string filter = _filter.Text.Trim();
        _mapSelect.Clear();
        foreach (var name in _maps)
            if (filter.Length == 0 || name.Contains(filter, System.StringComparison.OrdinalIgnoreCase))
                _mapSelect.AddItem(name);

        _nextButton.Disabled = _mapSelect.ItemCount == 0;
    }

    // ---- conversion ----

    private void OnNextPressed()
    {
        if (_mapSelect.ItemCount == 0) return;
        string map = _mapSelect.GetItemText(_mapSelect.Selected);
        string root = ProjectSettings.GlobalizePath(_pathEdit.Text.Trim());
        string ogz = Path.Combine(root, "packages", map + ".ogz");
        string outDir = $"{OutputRoot}/{Path.GetFileName(map)}";

        try
        {
            var map0 = World.Ogz.OgzReader.Read(ogz);
            var catalogue = OgzConverter.TryLoadCatalogue(CatalogueDir, Path.GetFileName(map));

            var snapshot = OgzConverter.Convert(map0, outDir, catalogue);
            string hash = MapStore.ContentHash(outDir);
            string manifestInfo = catalogue != null
                ? $"manifest: {catalogue.VSlots.Count} materials, {catalogue.MapModels.Count} models, {catalogue.Sounds.Count} sounds"
                : "no metadata catalogue — geometry/entities only";
            _result.Text = $"Converted '{map}' → {outDir}\n" +
                           $"entities: {snapshot.Entities.Count}\n{manifestInfo}\ncontent hash: {hash}";
            GD.Print($"[converter] {_result.Text}");
        }
        catch (System.Exception e)
        {
            _result.Text = $"Conversion failed for '{map}': {e.Message}";
            GD.PushError($"[converter] {e}");
        }
    }

    // ---- settings (user://settings.cfg) ----

    /// <summary>Read a value from <c>user://settings.cfg</c>, or empty if unset.</summary>
    public static string LoadSetting(string key)
    {
        var cfg = new ConfigFile();
        return cfg.Load(SettingsPath) == Error.Ok && cfg.HasSectionKey("settings", key)
            ? cfg.GetValue("settings", key).AsString()
            : "";
    }

    /// <summary>Write a value to <c>user://settings.cfg</c>, preserving other keys.</summary>
    public static void SaveSetting(string key, Variant value)
    {
        var cfg = new ConfigFile();
        cfg.Load(SettingsPath);
        cfg.SetValue("settings", key, value);
        cfg.Save(SettingsPath);
    }
}
