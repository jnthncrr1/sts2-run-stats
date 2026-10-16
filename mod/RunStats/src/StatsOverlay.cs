using System.Text.Json;
using Godot;

namespace RunStats;

/// <summary>
/// In-game panel built from plain Godot nodes (no Node subclass, so no Godot source
/// generator is needed). Reads the same current.json the browser viewer polls, so it
/// shares one source of truth with the tracker and holds no stats of its own.
/// Collapsed it shows a one-line header; hovering expands the full table. F2 hides it.
/// </summary>
public static class StatsOverlay
{
    private static readonly string[] Columns = { "Damage", "Block", "Prevented", "Weak", "Vuln" };
    private static readonly Color Text = new("efe6d8");
    private static readonly Color Muted = new("9a8f88");
    private static readonly Color Accent = new("e0a84a");
    private static readonly Color Panel = new("1f1b22e6");
    private static readonly Color Border = new("352e33");

    // One hue per character; copies of the same character step through the shades in net-ID order.
    private static readonly Dictionary<string, string[]> CharacterColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IRONCLAD"] = new[] { "d14343", "8f2c2c", "ea8a8a", "b07a7a" },
        ["SILENT"] = new[] { "5fcf6e", "3c9448", "a3e6ac", "86ad8c" },
        ["REGENT"] = new[] { "f2a23a", "b57322", "f8c98a", "c2a079" },
        ["NECROBINDER"] = new[] { "9a63e0", "6c42a3", "c3a3ee", "9f8ab8" },
        ["DEFECT"] = new[] { "4aa3f0", "2f72ad", "93c8f7", "7f9db5" },
    };
    private static readonly string[] FallbackColors = { "9a8f88", "6f6660", "c4b9b1", "4f4843" };

    private static CanvasLayer? _layer;
    private static PanelContainer? _root;
    private static Label? _status;
    private static GridContainer? _grid;
    private static bool _expanded;
    private static bool _hidden;
    private static bool _dragging;
    private static Vector2 _dragOffset;
    private static string _lastJson = "";
    private static int _combats;
    private static bool _hasPlayers;

    private static string SettingsPath => Path.Combine(StatsFile.Directory, "overlay.json");

    public static void EnsureMounted()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) return;
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null) return;

        _layer = new CanvasLayer { Name = "RunStatsOverlay", Layer = 100 };
        _root = BuildPanel();
        _layer.AddChild(_root);

        var timer = new Godot.Timer { WaitTime = 1.0, Autostart = true, OneShot = false };
        timer.Timeout += Refresh;
        _layer.AddChild(timer);

        tree.Root.CallDeferred(Node.MethodName.AddChild, _layer);
        _root.Ready += () => { LoadSettings(); Refresh(); };
    }

    public static void ToggleHidden()
    {
        _hidden = !_hidden;
        ApplyVisibility();
        SaveSettings();
    }

    private static PanelContainer BuildPanel()
    {
        var root = new PanelContainer { Name = "RunStats", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        root.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = Panel, BorderColor = Border,
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 8, ContentMarginBottom = 8,
        });
        root.MouseEntered += () => { _expanded = true; ApplyVisibility(); };
        root.MouseExited += () => { if (!_dragging) { _expanded = false; ApplyVisibility(); } };
        root.GuiInput += OnGuiInput;

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        root.AddChild(box);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 10);
        header.AddChild(MakeLabel("Run Stats", 14, Text));
        _status = MakeLabel("", 12, Muted);
        header.AddChild(_status);
        box.AddChild(header);

        _grid = new GridContainer { Columns = Columns.Length + 1, Visible = false };
        _grid.AddThemeConstantOverride("h_separation", 14);
        _grid.AddThemeConstantOverride("v_separation", 3);
        box.AddChild(_grid);
        IgnoreMouse(box);
        return root;
    }

    private static void Refresh()
    {
        if (_root == null || _grid == null || _status == null) return;
        try
        {
            if (!File.Exists(StatsFile.CurrentPath)) { _root.Visible = false; return; }
            string json = File.ReadAllText(StatsFile.CurrentPath);
            if (json == _lastJson) { ApplyVisibility(); return; }
            RunStatsSnapshot? s = JsonSerializer.Deserialize<RunStatsSnapshot>(json);
            if (s == null) return;
            _lastJson = json;

            _combats = s.CombatsFinished;
            _hasPlayers = s.Players.Count > 0;
            RebuildGrid(s);
            ApplyVisibility();
        }
        catch (Exception e)
        {
            GD.PushWarning($"[RunStats] overlay refresh failed: {e.Message}");
        }
    }

    private static void RebuildGrid(RunStatsSnapshot s)
    {
        foreach (Node child in _grid!.GetChildren()) child.QueueFree();

        _grid.AddChild(MakeLabel("Player", 12, Muted, 160));
        foreach (string c in Columns) _grid.AddChild(MakeLabel(c.ToUpperInvariant(), 11, Muted, 72, HorizontalAlignment.Right));

        var colors = AssignColors(s);
        var max = new long[Columns.Length];
        foreach (PlayerStats p in s.Players)
        {
            long[] v = Values(p);
            for (int i = 0; i < v.Length; i++) max[i] = Math.Max(max[i], v[i]);
        }

        foreach (PlayerStats p in s.Players)
        {
            var nameCell = new HBoxContainer();
            nameCell.AddThemeConstantOverride("separation", 8);
            var swatch = new ColorRect { Color = colors[p.NetId], CustomMinimumSize = new Vector2(10, 10), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            nameCell.AddChild(swatch);
            nameCell.AddChild(MakeLabel(p.Name, 13, Text, 142));
            _grid.AddChild(nameCell);

            long[] v = Values(p);
            for (int i = 0; i < v.Length; i++)
            {
                bool top = v[i] > 0 && v[i] == max[i] && s.Players.Count > 1;
                _grid.AddChild(MakeLabel(v[i].ToString("N0"), 13, top ? Accent : Text, 72, HorizontalAlignment.Right));
            }
        }
        IgnoreMouse(_grid);
    }

    private static void IgnoreMouse(Node node)
    {
        if (node is Control c) c.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (Node child in node.GetChildren()) IgnoreMouse(child);
    }

    private static long[] Values(PlayerStats p) => new[] { p.Damage, p.Block, p.DamagePrevented, p.Weak, p.Vulnerable };

    private static Dictionary<ulong, Color> AssignColors(RunStatsSnapshot s)
    {
        var result = new Dictionary<ulong, Color>();
        var copies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (PlayerStats p in s.Players.OrderBy(p => p.NetId))
        {
            string ch = p.Character ?? "";
            int n = copies[ch] = copies.GetValueOrDefault(ch) + 1;
            string[] ramp = CharacterColors.GetValueOrDefault(ch, FallbackColors);
            result[p.NetId] = new Color(ramp[Math.Min(n - 1, ramp.Length - 1)]);
        }
        return result;
    }

    private static void ApplyVisibility()
    {
        if (_root == null || _grid == null) return;
        _root.Visible = _hasPlayers && !_hidden;
        _grid.Visible = _expanded;
        if (_status != null) _status.Text = $"{_combats} combats · {(_expanded ? "F2 hides" : "hover to expand")}";
        _root.CallDeferred(Control.MethodName.ResetSize);
        CallClamp();
    }

    private static void OnGuiInput(InputEvent e)
    {
        if (_root == null) return;
        switch (e)
        {
            case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left:
                _dragging = mb.Pressed;
                if (mb.Pressed) _dragOffset = _root.GetViewport().GetMousePosition() - _root.Position;
                else SaveSettings();
                _root.GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseMotion when _dragging:
                _root.Position = _root.GetViewport().GetMousePosition() - _dragOffset;
                CallClamp();
                _root.GetViewport().SetInputAsHandled();
                break;
        }
    }

    private static void CallClamp()
    {
        if (_root == null || !_root.IsInsideTree()) return;
        Vector2 vp = _root.GetViewport().GetVisibleRect().Size;
        Vector2 sz = _root.Size;
        _root.Position = new Vector2(
            Mathf.Clamp(_root.Position.X, 0, Mathf.Max(0, vp.X - sz.X)),
            Mathf.Clamp(_root.Position.Y, 0, Mathf.Max(0, vp.Y - sz.Y)));
    }

    private static Label MakeLabel(string text, int size, Color color, float minWidth = 0, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var l = new Label { Text = text, HorizontalAlignment = align, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        if (minWidth > 0) l.CustomMinimumSize = new Vector2(minWidth, 0);
        return l;
    }

    private sealed class Settings
    {
        public float X { get; set; } = 16;
        public float Y { get; set; } = 140;
        public bool Hidden { get; set; }
    }

    private static void LoadSettings()
    {
        var s = new Settings();
        try
        {
            if (File.Exists(SettingsPath)) s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? s;
        }
        catch (Exception e) { GD.PushWarning($"[RunStats] overlay settings unreadable, using defaults: {e.Message}"); }
        _hidden = s.Hidden;
        if (_root != null) _root.Position = new Vector2(s.X, s.Y);
    }

    private static void SaveSettings()
    {
        if (_root == null) return;
        try
        {
            Directory.CreateDirectory(StatsFile.Directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings { X = _root.Position.X, Y = _root.Position.Y, Hidden = _hidden }));
        }
        catch (Exception e) { GD.PushWarning($"[RunStats] could not save overlay settings: {e.Message}"); }
    }
}
