using System;
using System.Text.Json.Nodes;
using GBNG.Domain;
using Godot;

namespace GBNG.App;

public sealed partial class LegacyWorldSettingsWindow : Window
{
    private static readonly (string Value, string Label)[] SkyTypes =
    [
        ("Day", "Day"),
        ("Space", "Space"),
        ("Overcast", "Overcast"),
        ("SolidColor", "Solid color")
    ];
    private static readonly (string Value, string Label)[] LightingModes =
    [
        ("Day", "Day"),
        ("Night", "Night"),
        ("Dark", "Dark")
    ];

    private static readonly (string Id, string Label, float Width, float Depth)[] MapSizes =
    [
        ("small", "Small · 50 × 50", 50f, 50f),
        ("medium", "Medium · 200 × 200", 200f, 200f),
        ("large", "Large · 500 × 500", 500f, 500f),
        ("long", "Long · 50 × 5000", 50f, 5000f)
    ];

    private readonly Action<string, JsonNode> _setStageProperty;
    private readonly Action<float, float> _setMapSize;
    private readonly OptionButton _skyType = new();
    private readonly ColorPickerButton _skyColor = new() { EditAlpha = false };
    private readonly OptionButton _lightingMode = new();
    private readonly Label _mapSizeSummary = new();
    private readonly Button _mapSizeButton = new();
    private readonly OptionButton _mapSizeChoice = new();
    private readonly Window _mapSizeDialog = new();
    private readonly Button _mapSizeConfirm = new();
    private float _mapWidth = 500f;
    private float _mapDepth = 500f;
    private bool _refreshing;

    public LegacyWorldSettingsWindow(Action<string, JsonNode> setStageProperty, Action<float, float> setMapSize)
    {
        _setStageProperty = setStageProperty;
        _setMapSize = setMapSize;
        Name = "WorldSettingsWindow";
        Title = "World settings";
        Size = new Vector2I(500, 360);
        CloseRequested += () =>
        {
            _mapSizeDialog.Hide();
            Hide();
        };
        BuildInterface();
        RefreshFromStage(null);
    }

    public void RefreshFromStage(JsonObject? stage)
    {
        _refreshing = true;
        try
        {
            PopulateOptions(_skyType, SkyTypes, LegacySaveDocument.ReadString(stage?["skyType"], "Day"));
            _skyColor.Color = ReadStageColor(stage?["skyColor"]);
            PopulateOptions(_lightingMode, LightingModes, LegacySaveDocument.ReadString(stage?["sceneLightingMode"], "Day"));
            _mapWidth = ReadStageDimension(stage?["groundSizeX"]);
            _mapDepth = ReadStageDimension(stage?["groundSizeZ"]);
            _mapSizeSummary.Text = $"{_mapWidth:0.##} × {_mapDepth:0.##}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void BuildInterface()
    {
        var margin = new MarginContainer { AnchorRight = 1f, AnchorBottom = 1f };
        margin.AddThemeConstantOverride("margin_left", 18);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        AddChild(margin);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 12);
        margin.AddChild(layout);
        layout.AddChild(new Label
        {
            Text = "Change the project sky and scene lighting. Solid color uses the selected sky color.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });

        _skyType.Name = "WorldSettingsSkyType";
        _skyType.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        PopulateOptions(_skyType, SkyTypes, "Day");
        _skyType.ItemSelected += index =>
        {
            if (!_refreshing)
                _setStageProperty("skyType", JsonValue.Create(_skyType.GetItemMetadata((int)index).AsString())!);
        };
        layout.AddChild(CreateFieldRow("Sky type", _skyType));

        _skyColor.Name = "WorldSettingsSkyColor";
        _skyColor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _skyColor.ColorChanged += color =>
        {
            if (_refreshing)
                return;
            _setStageProperty("skyColor", new JsonObject
            {
                ["r"] = color.R,
                ["g"] = color.G,
                ["b"] = color.B,
                ["a"] = 1f
            });
        };
        layout.AddChild(CreateFieldRow("Sky color", _skyColor));

        _lightingMode.Name = "WorldSettingsLightingMode";
        _lightingMode.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        PopulateOptions(_lightingMode, LightingModes, "Day");
        _lightingMode.ItemSelected += index =>
        {
            if (!_refreshing)
                _setStageProperty("sceneLightingMode", JsonValue.Create(_lightingMode.GetItemMetadata((int)index).AsString())!);
        };
        layout.AddChild(CreateFieldRow("Scene lighting", _lightingMode));
        var mapSizeControls = new HBoxContainer();
        _mapSizeSummary.Name = "WorldMapSizeSummary";
        _mapSizeSummary.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        mapSizeControls.AddChild(_mapSizeSummary);
        _mapSizeButton.Name = "WorldMapSizeButton";
        _mapSizeButton.Text = "Change…";
        _mapSizeButton.Pressed += OpenMapSizeDialog;
        mapSizeControls.AddChild(_mapSizeButton);
        layout.AddChild(CreateFieldRow("Map size", mapSizeControls));


        layout.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        var footer = new HBoxContainer();
        footer.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var close = new Button { Name = "WorldSettingsClose", Text = "Close" };
        close.Pressed += () => Hide();
        footer.AddChild(close);
        layout.AddChild(footer);
        BuildMapSizeDialog();
    }
    private void BuildMapSizeDialog()
    {
        _mapSizeDialog.Name = "WorldMapSizeDialog";
        _mapSizeDialog.Title = "Change map size";
        _mapSizeDialog.Size = new Vector2I(560, 230);
        _mapSizeDialog.CloseRequested += () => _mapSizeDialog.Hide();

        var margin = new MarginContainer { AnchorRight = 1f, AnchorBottom = 1f };
        margin.AddThemeConstantOverride("margin_left", 18);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        _mapSizeDialog.AddChild(margin);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 12);
        margin.AddChild(layout);
        layout.AddChild(new Label
        {
            Text = "Shrinking destroys terrain outside the new bounds. The centered terrain that fits is preserved; use Undo to reverse the resize.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });

        _mapSizeChoice.Name = "WorldMapSizeChoice";
        _mapSizeChoice.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _mapSizeChoice.ItemSelected += index => _mapSizeConfirm.Disabled = index >= MapSizes.Length;
        layout.AddChild(CreateFieldRow("Preset", _mapSizeChoice));

        layout.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        var footer = new HBoxContainer();
        footer.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var cancel = new Button { Name = "WorldMapSizeCancel", Text = "Cancel" };
        cancel.Pressed += () => _mapSizeDialog.Hide();
        footer.AddChild(cancel);
        _mapSizeConfirm.Name = "WorldMapSizeConfirm";
        _mapSizeConfirm.Text = "Resize";
        _mapSizeConfirm.Pressed += ConfirmMapSize;
        footer.AddChild(_mapSizeConfirm);
        layout.AddChild(footer);
        AddChild(_mapSizeDialog);
    }

    private void OpenMapSizeDialog()
    {
        _mapSizeChoice.Clear();
        int selectedIndex = -1;
        for (int index = 0; index < MapSizes.Length; index++)
        {
            (string id, string label, float width, float depth) = MapSizes[index];
            _mapSizeChoice.AddItem(label);
            _mapSizeChoice.SetItemMetadata(index, id);
            if (Mathf.IsEqualApprox(_mapWidth, width) && Mathf.IsEqualApprox(_mapDepth, depth))
                selectedIndex = index;
        }
        if (selectedIndex < 0)
        {
            _mapSizeChoice.AddItem($"Current · {_mapWidth:0.##} × {_mapDepth:0.##}");
            _mapSizeChoice.SetItemMetadata(_mapSizeChoice.ItemCount - 1, "current");
            selectedIndex = _mapSizeChoice.ItemCount - 1;
        }
        _mapSizeChoice.Select(selectedIndex);
        _mapSizeConfirm.Disabled = selectedIndex >= MapSizes.Length;
        _mapSizeDialog.PopupCentered(new Vector2I(560, 230));
    }

    private void ConfirmMapSize()
    {
        int selectedIndex = _mapSizeChoice.Selected;
        if ((uint)selectedIndex >= (uint)MapSizes.Length)
            return;
        var mapSize = MapSizes[selectedIndex];
        _mapSizeDialog.Hide();
        _setMapSize(mapSize.Width, mapSize.Depth);
    }

    private static float ReadStageDimension(JsonNode? node)
    {
        float size = LegacySaveDocument.ReadFloat(node, 500f);
        return size > 0f ? size : 500f;
    }


    private static HBoxContainer CreateFieldRow(string label, Control editor)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(135f, 0f), VerticalAlignment = VerticalAlignment.Center });
        row.AddChild(editor);
        return row;
    }

    private static void PopulateOptions(OptionButton picker, (string Value, string Label)[] options, string selectedValue)
    {
        picker.Clear();
        int selectedIndex = -1;
        foreach ((string value, string label) in options)
        {
            picker.AddItem(label);
            picker.SetItemMetadata(picker.ItemCount - 1, value);
            if (value == selectedValue)
                selectedIndex = picker.ItemCount - 1;
        }
        if (selectedIndex < 0)
        {
            picker.AddItem($"Unknown · {selectedValue}");
            picker.SetItemMetadata(picker.ItemCount - 1, selectedValue);
            selectedIndex = picker.ItemCount - 1;
        }
        picker.Select(selectedIndex);
    }

    private static Color ReadStageColor(JsonNode? node)
    {
        if (node is not JsonObject color)
            return new Color(0.5f, 0.5f, 0.5f, 1f);
        return new Color(
            LegacySaveDocument.ReadFloat(color["r"], 0.5f),
            LegacySaveDocument.ReadFloat(color["g"], 0.5f),
            LegacySaveDocument.ReadFloat(color["b"], 0.5f),
            1f);
    }
}
