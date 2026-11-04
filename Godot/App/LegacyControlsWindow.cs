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

    private readonly System.Action<bool, bool, bool, float, float> _applyPreferences;
    private readonly System.Action<Vector2I> _applyWindowedSize;
    private readonly CheckButton _showTooltips = new();
    private readonly CheckButton _fullscreenMode = new();
    private readonly OptionButton _windowedSize = new();
    private readonly Label _currentDisplaySize = new();
    private readonly CheckButton _invertVerticalLook = new();
    private readonly HSlider _lookSensitivity = new();
    private readonly HSlider _wheelSensitivity = new();
    private readonly Label _lookSensitivityValue = new();
    private readonly Label _wheelSensitivityValue = new();
    private Vector2I[] _windowedSizes = System.Array.Empty<Vector2I>();
    private bool _refreshing;
    public LegacyControlsWindow(System.Action<bool, bool, bool, float, float> applyPreferences, System.Action<Vector2I> applyWindowedSize)
    {
        _applyPreferences = applyPreferences;
        _applyWindowedSize = applyWindowedSize;
        Name = "ControlsWindow";
        Title = "Options";
        Size = new Vector2I(560, 500);
        Hide();
        CloseRequested += () => Hide();
        BuildInterface();
        SetPreferences(true, false, false, DefaultMouseLookSensitivity, DefaultWheelSensitivitySliderValue);
    }

    internal static float DefaultWheelSensitivitySliderValue
    {
        get
        {
            GetWheelSensitivityBounds(out float minimum, out float maximum, out float defaultValue);
            return Mathf.InverseLerp(minimum, maximum, defaultValue);
        }
    }


    public void SetPreferences(bool showTooltips, bool fullscreen, bool invertVerticalLook, float lookSensitivity, float wheelSensitivity)
    {
        _refreshing = true;
        _showTooltips.ButtonPressed = showTooltips;
        _fullscreenMode.ButtonPressed = fullscreen;
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
    public void SetDisplayOptions(Vector2I[] windowedSizes, Vector2I selectedWindowedSize, Vector2I currentSize, bool fullscreen)
    {
        _windowedSizes = windowedSizes;
        _refreshing = true;
        _windowedSize.Clear();
        int selectedIndex = _windowedSizes.Length;
        for (int index = 0; index < _windowedSizes.Length; index++)
        {
            Vector2I size = _windowedSizes[index];
            _windowedSize.AddItem($"{size.X} × {size.Y}");
            if (size == selectedWindowedSize)
                selectedIndex = index;
        }
        _windowedSize.AddItem($"Custom ({selectedWindowedSize.X} × {selectedWindowedSize.Y})");
        _windowedSize.Disabled = fullscreen;
        _windowedSize.Select(selectedIndex);
        RefreshDisplayLabel(currentSize, fullscreen);
        _refreshing = false;
    }

    public void RefreshDisplayState(Vector2I selectedWindowedSize, Vector2I currentSize, bool fullscreen)
    {
        _refreshing = true;
        int selectedIndex = _windowedSizes.Length;
        for (int index = 0; index < _windowedSizes.Length; index++)
        {
            if (_windowedSizes[index] != selectedWindowedSize)
                continue;
            selectedIndex = index;
            break;
        }
        if (_windowedSize.ItemCount > _windowedSizes.Length)
        {
            _windowedSize.SetItemText(
                _windowedSizes.Length,
                $"Custom ({selectedWindowedSize.X} × {selectedWindowedSize.Y})");
            _windowedSize.Select(selectedIndex);
        }
        _windowedSize.Disabled = fullscreen;
        RefreshDisplayLabel(currentSize, fullscreen);
        _refreshing = false;
    }

    internal int WindowedSizePresetCount => _windowedSizes.Length;
    internal Vector2I GetWindowedSizePreset(int index) => _windowedSizes[index];

    private void RefreshDisplayLabel(Vector2I currentSize, bool fullscreen)
    {
        _currentDisplaySize.Text = fullscreen
            ? $"Fullscreen display: {currentSize.X} × {currentSize.Y}"
            : $"Current window size: {currentSize.X} × {currentSize.Y}";
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
            Text = "Adjust display mode, camera controls, and editor help preferences. These settings are saved on this device.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });

        _fullscreenMode.Name = "OptionsFullscreen";
        _fullscreenMode.Text = "Fullscreen display mode";
        _fullscreenMode.Toggled += _ => ApplyCurrentPreferences();
        layout.AddChild(_fullscreenMode);

        _windowedSize.Name = "OptionsWindowedSize";
        _windowedSize.ItemSelected += OnWindowedSizeSelected;
        var windowedSizeRow = new HBoxContainer();
        windowedSizeRow.AddChild(new Label { Text = "Windowed size", CustomMinimumSize = new Vector2(190f, 0f) });
        _windowedSize.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        windowedSizeRow.AddChild(_windowedSize);
        layout.AddChild(windowedSizeRow);
        _currentDisplaySize.Name = "OptionsCurrentDisplaySize";
        layout.AddChild(_currentDisplaySize);
        layout.AddChild(new Label
        {
            Text = "Windowed sizes only. Fullscreen uses the monitor's current display mode.",
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
        var reset = new Button { Name = "ControlsResetDefaults", Text = "Reset camera controls" };
        reset.Pressed += ResetToDefaults;
        footer.AddChild(reset);
        var close = new Button { Name = "ControlsClose", Text = "Close" };
        close.Pressed += Hide;
        footer.AddChild(close);
        layout.AddChild(footer);
    }

    private void OnWindowedSizeSelected(long index)
    {
        if (_refreshing || index < 0 || index >= _windowedSizes.Length)
            return;
        _applyWindowedSize(_windowedSizes[(int)index]);
    }
    private void ApplyCurrentPreferences()
    {
        if (_refreshing)
            return;
        RefreshSensitivityLabels();
        _applyPreferences(
            _showTooltips.ButtonPressed,
            _fullscreenMode.ButtonPressed,
            _invertVerticalLook.ButtonPressed,
            (float)_lookSensitivity.Value,
            (float)_wheelSensitivity.Value);
    }

    private void ResetToDefaults()
    {
        SetPreferences(
            _showTooltips.ButtonPressed,
            _fullscreenMode.ButtonPressed,
            false,
            DefaultMouseLookSensitivity,
            DefaultWheelSensitivitySliderValue);
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
