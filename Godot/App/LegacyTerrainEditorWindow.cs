using Godot;

namespace GBNG.App;

public enum LegacyTerrainBrushMode
{
    Create,
    Dig,
    Paint
}

public readonly record struct LegacyTerrainBrush(LegacyTerrainBrushMode Mode, int Shape, int Direction, int Style);

public sealed partial class LegacyTerrainEditorWindow : Window
{
    private readonly CheckBox _active = new();
    private readonly OptionButton _mode = new();
    private readonly OptionButton _shape = new();
    private readonly OptionButton _direction = new();
    private readonly OptionButton _style = new();

    public LegacyTerrainEditorWindow()
    {
        Title = "Terrain editor";
        Size = new Vector2I(380, 560);
        CloseRequested += () =>
        {
            SetBrushActive(false);
            Hide();
        };

        var margin = new MarginContainer { AnchorRight = 1f, AnchorBottom = 1f };
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        AddChild(margin);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 8);
        margin.AddChild(layout);
        layout.AddChild(new Label
        {
            Text = "Enable the brush, then click or drag over terrain in the viewport. Brushes write to the original .voos terrain grid.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });
        _active.Name = "TerrainBrushActive";
        _active.Text = "Brush active";
        _active.Toggled += _ => ActiveChanged?.Invoke(IsBrushActive);
        layout.AddChild(_active);

        AddPicker(layout, _mode, "Mode", ["Create", "Dig", "Paint"]);
        AddPicker(layout, _shape, "Shape", ["Full", "Half", "Ramp", "Corner"]);
        AddPicker(layout, _direction, "Direction", ["North", "East", "South", "West"]);
        string[] styles =
        [
            "White", "Gray", "Red", "Yellow", "Green", "Cyan", "Blue", "Magenta",
            "Light blue", "Sand yellow", "Orange", "Coral", "Dark red", "Mint", "Teal", "Pink",
            "Stone", "Space", "Grass", "Snow rock", "Dirt", "Grass stone", "Grey craters", "Ice",
            "Lava", "Sand", "Water", "Wooden", "Red craters", "Industrial green", "Industrial red",
            "Grey bricks", "Metal beige", "Red bricks", "Road", "Road crossing", "Road white broken line",
            "Road yellow unbroken line", "Pavement", "Pavement concave corner", "Pavement convex corner"
        ];
        AddPicker(layout, _style, "Style", styles);
        _style.Select(16);
        layout.AddChild(new Label
        {
            Text = "Create adds beside occupied terrain. Dig removes a cell. Paint changes only an existing cell's style. Ctrl+Z / Ctrl+Y undo and redo completed strokes.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });
    }

    public event Action<bool>? ActiveChanged;
    public bool IsBrushActive => _active.ButtonPressed;
    public LegacyTerrainBrush Brush => new(
        (LegacyTerrainBrushMode)Math.Max(0, _mode.GetSelectedId()),
        Math.Max(0, _shape.GetSelectedId()),
        Math.Max(0, _direction.GetSelectedId()),
        Math.Max(0, _style.GetSelectedId()));

    public void SetBrushActive(bool active)
    {
        if (_active.ButtonPressed == active)
            return;
        _active.ButtonPressed = active;
        ActiveChanged?.Invoke(active);
    }

    private static void AddPicker(VBoxContainer parent, OptionButton picker, string label, string[] options)
    {
        picker.Name = $"Terrain{label.Replace(" ", "")}";
        parent.AddChild(new Label { Text = label });
        for (int index = 0; index < options.Length; index++)
            picker.AddItem(options[index], index);
        picker.Select(0);
        parent.AddChild(picker);
    }
}
