using Godot;

namespace GBNG.App;

public sealed partial class LegacyControlsWindow : Window
{
    internal const float DefaultMouseLookSensitivity = 1f;
    internal const float MinimumMouseLookSensitivity = 0.1f;
    internal const float MaximumMouseLookSensitivity = 3f;

    private const float WindowsWheelSensitivityMinimum = 0.1f;
    private const float WindowsWheelSensitivityMaximum = 1f;
    private const float WindowsWheelSensitivityDefault = 0.5f;
    private const float MacWheelSensitivityMinimum = 0.003f;
    private const float MacWheelSensitivityMaximum = 0.03f;
    private const float MacWheelSensitivityDefault = 0.020f;

    private readonly System.Action<bool, bool, float, float> _applyPreferences;
    private readonly CheckButton _showTooltips = new();
    private readonly CheckButton _invertVerticalLook = new();
    private readonly HSlider _lookSensitivity = new();
    private readonly HSlider _wheelSensitivity = new();
    private readonly Label _lookSensitivityValue = new();
    private readonly Label _wheelSensitivityValue = new();
    private bool _refreshing;

    internal static float DefaultWheelSensitivitySliderValue
    {
        get
        {
            GetWheelSensitivityBounds(out float minimum, out float maximum, out float defaultValue);
            return Mathf.InverseLerp(minimum, maximum, defaultValue);
        }
    }

    public LegacyControlsWindow(System.Action<bool, bool, float, float> applyPreferences)
    {
        _applyPreferences = applyPreferences;
        Name = "ControlsWindow";
        Title = "Controls";
        Size = new Vector2I(560, 380);
        Hide();
        CloseRequested += () => Hide();
        BuildInterface();
        SetPreferences(true, false, DefaultMouseLookSensitivity, DefaultWheelSensitivitySliderValue);
    }

    public void SetPreferences(bool showTooltips, bool invertVerticalLook, float lookSensitivity, float wheelSensitivity)
    {
        _refreshing = true;
        _showTooltips.ButtonPressed = showTooltips;
        _invertVerticalLook.ButtonPressed = invertVerticalLook;
        _lookSensitivity.Value = ClampFinite(
            lookSensitivity,
            MinimumMouseLookSensitivity,
            MaximumMouseLookSensitivity,
            DefaultMouseLookSensitivity);
        _wheelSensitivity.Value = ClampFinite(wheelSensitivity, 0f, 1f, DefaultWheelSensitivitySliderValue);
        _refreshing = false;
        RefreshSensitivityLabels();
    }

    internal static float WheelZoomSensitivityMultiplier(float sliderValue)
    {
        GetWheelSensitivityBounds(out float minimum, out float maximum, out float defaultValue);
        float value = ClampFinite(sliderValue, 0f, 1f, DefaultWheelSensitivitySliderValue);
        return Mathf.Lerp(minimum, maximum, value) / defaultValue;
    }

    private void BuildInterface()
    {
        var margin = new MarginContainer { AnchorRight = 1f, AnchorBottom = 1f };
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        AddChild(margin);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 14);
        margin.AddChild(layout);
        layout.AddChild(new Label
        {
            Text = "Adjust camera controls and editor help tooltips. These preferences are saved on this device.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });

        _showTooltips.Name = "ControlsShowTooltips";
        _showTooltips.Text = "Show editor help tooltips";
        _showTooltips.Toggled += _ => ApplyCurrentPreferences();
        layout.AddChild(_showTooltips);

        _invertVerticalLook.Name = "ControlsInvertVerticalLook";
        _invertVerticalLook.Text = "Invert vertical mouse look";
        _invertVerticalLook.Toggled += _ => ApplyCurrentPreferences();
        layout.AddChild(_invertVerticalLook);

        _lookSensitivity.Name = "ControlsLookSensitivity";
        _lookSensitivity.MinValue = MinimumMouseLookSensitivity;
        _lookSensitivity.MaxValue = MaximumMouseLookSensitivity;
        _lookSensitivity.Step = 0.0;
        _lookSensitivity.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _lookSensitivity.ValueChanged += _ => ApplyCurrentPreferences();
        _lookSensitivityValue.Name = "ControlsLookSensitivityValue";
        layout.AddChild(CreateSliderRow("Mouse look sensitivity", _lookSensitivity, _lookSensitivityValue));

        _wheelSensitivity.Name = "ControlsWheelSensitivity";
        _wheelSensitivity.MinValue = 0f;
        _wheelSensitivity.MaxValue = 1f;
        _wheelSensitivity.Step = 0.0;
        _wheelSensitivity.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _wheelSensitivity.ValueChanged += _ => ApplyCurrentPreferences();
        _wheelSensitivityValue.Name = "ControlsWheelSensitivityValue";
        layout.AddChild(CreateSliderRow("Mouse wheel sensitivity", _wheelSensitivity, _wheelSensitivityValue));

        layout.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        var footer = new HBoxContainer();
        footer.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var reset = new Button { Name = "ControlsResetDefaults", Text = "Reset to defaults" };
        reset.Pressed += ResetToDefaults;
        footer.AddChild(reset);
        var close = new Button { Name = "ControlsClose", Text = "Close" };
        close.Pressed += Hide;
        footer.AddChild(close);
        layout.AddChild(footer);
    }

    private void ApplyCurrentPreferences()
    {
        if (_refreshing)
            return;
        RefreshSensitivityLabels();
        _applyPreferences(
            _showTooltips.ButtonPressed,
            _invertVerticalLook.ButtonPressed,
            (float)_lookSensitivity.Value,
            (float)_wheelSensitivity.Value);
    }

    private void ResetToDefaults()
    {
        SetPreferences(_showTooltips.ButtonPressed, false, DefaultMouseLookSensitivity, DefaultWheelSensitivitySliderValue);
        ApplyCurrentPreferences();
    }

    private void RefreshSensitivityLabels()
    {
        _lookSensitivityValue.Text = $"{_lookSensitivity.Value:0.00}×";
        _wheelSensitivityValue.Text = $"{WheelZoomSensitivityMultiplier((float)_wheelSensitivity.Value):0.00}×";
    }

    private static Control CreateSliderRow(string label, HSlider slider, Label value)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(190f, 0f) });
        row.AddChild(slider);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.CustomMinimumSize = new Vector2(50f, 0f);
        row.AddChild(value);
        return row;
    }

    private static float ClampFinite(float value, float minimum, float maximum, float fallback)
        => float.IsFinite(value) ? Mathf.Clamp(value, minimum, maximum) : fallback;

    private static void GetWheelSensitivityBounds(out float minimum, out float maximum, out float defaultValue)
    {
        if (OperatingSystem.IsMacOS())
        {
            minimum = MacWheelSensitivityMinimum;
            maximum = MacWheelSensitivityMaximum;
            defaultValue = MacWheelSensitivityDefault;
        }
        else
        {
            minimum = WindowsWheelSensitivityMinimum;
            maximum = WindowsWheelSensitivityMaximum;
            defaultValue = WindowsWheelSensitivityDefault;
        }
    }
}
