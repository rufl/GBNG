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

    private readonly Action<string, JsonNode> _setStageProperty;
    private readonly OptionButton _skyType = new();
    private readonly ColorPickerButton _skyColor = new() { EditAlpha = false };
    private readonly OptionButton _lightingMode = new();
    private bool _refreshing;

    public LegacyWorldSettingsWindow(Action<string, JsonNode> setStageProperty)
    {
        _setStageProperty = setStageProperty;
        Name = "WorldSettingsWindow";
        Title = "World settings";
        Size = new Vector2I(500, 360);
        CloseRequested += () => Hide();
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

        layout.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        var footer = new HBoxContainer();
        footer.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var close = new Button { Name = "WorldSettingsClose", Text = "Close" };
        close.Pressed += () => Hide();
        footer.AddChild(close);
        layout.AddChild(footer);
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
