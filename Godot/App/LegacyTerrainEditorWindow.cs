using GBNG.World;
using Godot;

namespace GBNG.App;

public enum LegacyTerrainBrushMode
{
    Create,
    Dig,
    Paint,
    Select
}

public readonly record struct LegacyTerrainBrush(LegacyTerrainBrushMode Mode, int Shape, int Direction, int Style);

public sealed partial class LegacyTerrainEditorWindow : Window
{
    private readonly CheckBox _active = new();
    private readonly OptionButton _mode = new();
    private readonly OptionButton _shape = new();
    private readonly OptionButton _direction = new();
    private readonly OptionButton _style = new();
    private readonly Button _importStyleButton = new();
    private readonly Button _copySelectionButton = new();
    private readonly Button _clearSelectionButton = new();
    private readonly Label _selectionStatus = new();
    private static readonly string[] BuiltInStyleNames =
    [
        "White", "Gray", "Red", "Yellow", "Green", "Cyan", "Blue", "Magenta",
        "Light blue", "Sand yellow", "Orange", "Coral", "Dark red", "Mint", "Teal", "Pink",
        "Stone", "Space", "Grass", "Snow rock", "Dirt", "Grass stone", "Grey craters", "Ice",
        "Lava", "Sand", "Water", "Wooden", "Red craters", "Industrial green", "Industrial red",
        "Grey bricks", "Metal beige", "Red bricks", "Road", "Road crossing", "Road white broken line",
        "Road yellow unbroken line", "Pavement", "Pavement concave corner", "Pavement convex corner"
    ];

    public LegacyTerrainEditorWindow()
    {
        Title = "Terrain editor";
        Size = new Vector2I(380, 660);
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

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        margin.AddChild(scroll);
        var layout = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        layout.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(layout);
        layout.AddChild(new Label
        {
            Text = "Brush active: click or drag to edit the original .voos terrain grid.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });
        _active.Name = "TerrainBrushActive";
        _active.Text = "Brush active";
        _active.Toggled += _ => ActiveChanged?.Invoke(IsBrushActive);
        layout.AddChild(_active);

        AddPicker(layout, _mode, "Mode", ["Create", "Dig", "Paint", "Select"]);
        _mode.ItemSelected += _ => BrushChanged?.Invoke(Brush);
        AddPicker(layout, _shape, "Shape", ["Full", "Half", "Ramp", "Corner"]);
        AddPicker(layout, _direction, "Direction", ["North", "East", "South", "West"]);
        _style.Name = "TerrainStyle";
        layout.AddChild(new Label { Text = "Style" });
        layout.AddChild(_style);
        _style.ItemSelected += _ => BrushChanged?.Invoke(Brush);
        _importStyleButton.Name = "TerrainImportStyleButton";
        _importStyleButton.Text = "Import local texture…";
        _importStyleButton.Pressed += () => ImportStyleRequested?.Invoke();
        layout.AddChild(_importStyleButton);
        SetCustomStyles(Array.Empty<LegacyTerrainStyleOption>(), 16);
        _selectionStatus.Name = "TerrainSelectionStatus";
        _selectionStatus.Text = "No terrain cells selected.";
        _selectionStatus.AutowrapMode = TextServer.AutowrapMode.Word;
        layout.AddChild(_selectionStatus);
        var selectionActions = new HBoxContainer();
        _copySelectionButton.Name = "TerrainCopySelectionButton";
        _copySelectionButton.Text = "Copy selection";
        _copySelectionButton.Disabled = true;
        _copySelectionButton.Pressed += () => CopySelectionRequested?.Invoke();
        selectionActions.AddChild(_copySelectionButton);
        _clearSelectionButton.Name = "TerrainClearSelectionButton";
        _clearSelectionButton.Text = "Clear selection";
        _clearSelectionButton.Disabled = true;
        _clearSelectionButton.Pressed += () => ClearSelectionRequested?.Invoke();
        selectionActions.AddChild(_clearSelectionButton);
        layout.AddChild(selectionActions);
        layout.AddChild(new Label
        {
            Text = "Select: drag over cells. Copy, move, click to paste; Esc cancels.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });
        layout.AddChild(new Label
        {
            Text = "Create, Dig, and Paint edit cells. Ctrl+Z/Y undo or redo strokes and pastes.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });
    }

    public event Action<bool>? ActiveChanged;
    public event Action<LegacyTerrainBrush>? BrushChanged;
    public event Action? ImportStyleRequested;
    public event Action? CopySelectionRequested;
    public event Action? ClearSelectionRequested;
    public bool IsBrushActive => _active.ButtonPressed;
    public LegacyTerrainBrush Brush => new(
        (LegacyTerrainBrushMode)Math.Max(0, _mode.GetSelectedId()),
        Math.Max(0, _shape.GetSelectedId()),
        Math.Max(0, _direction.GetSelectedId()),
        Math.Max(0, _style.GetSelectedId()));

    public void SetCustomStyles(IReadOnlyList<LegacyTerrainStyleOption> customStyles, int selectedStyle)
    {
        _style.Clear();
        for (int index = 0; index < BuiltInStyleNames.Length; index++)
            _style.AddItem(BuiltInStyleNames[index], index);
        foreach (LegacyTerrainStyleOption style in customStyles)
        {
            string label = style.AssetUri is null ? $"{style.Name} · local texture required" : style.Name;
            _style.AddItem(label, style.Id);
        }
        SelectStyle(selectedStyle);
    }

    public void SelectStyle(int styleId)
    {
        for (int index = 0; index < _style.ItemCount; index++)
        {
            if (_style.GetItemId(index) == styleId)
            {
                _style.Select(index);
                return;
            }
        }
        _style.Select(16);
    }

    public void SetSelectionStatus(int cellCount, bool copying)
    {
        _selectionStatus.Text = cellCount == 0
            ? "No terrain cells selected."
            : copying
                ? $"{cellCount} cells selected. Click the viewport to paste; Esc cancels."
                : $"{cellCount} terrain cells selected.";
        _copySelectionButton.Disabled = cellCount == 0;
        _clearSelectionButton.Disabled = cellCount == 0;
    }

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
