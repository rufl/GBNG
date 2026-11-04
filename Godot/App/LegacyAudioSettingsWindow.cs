using Godot;
using GBNG.Audio;

namespace GBNG.App;

public sealed partial class LegacyAudioSettingsWindow : Window
{
    private readonly System.Action<float> _applySfxPreference;
    private readonly System.Action<float> _applyMusicPreference;
    private readonly HSlider _sfxVolume = new();
    private readonly Label _sfxVolumeValue = new();
    private readonly HSlider _musicVolume = new();
    private readonly Label _musicVolumeValue = new();
    private bool _refreshing;

    public LegacyAudioSettingsWindow(System.Action<float> applySfxPreference, System.Action<float> applyMusicPreference)
    {
        _applySfxPreference = applySfxPreference;
        _applyMusicPreference = applyMusicPreference;
        Name = "AudioSettingsWindow";
        Title = "Audio";
        Size = new Vector2I(540, 285);
        Hide();
        CloseRequested += Hide;
        BuildInterface();
        SetSfxVolume(LegacyAudioSettings.DefaultSfxVolume);
        SetMusicVolume(LegacyAudioSettings.DefaultMusicVolume);
    }

    public void SetSfxVolume(float volume)
    {
        _refreshing = true;
        _sfxVolume.Value = LegacyAudioSettings.ClampSfxVolume(volume);
        _refreshing = false;
        RefreshVolumeLabel(_sfxVolume, _sfxVolumeValue);
    }

    public void SetMusicVolume(float volume)
    {
        _refreshing = true;
        _musicVolume.Value = LegacyAudioSettings.ClampMusicVolume(volume);
        _refreshing = false;
        RefreshVolumeLabel(_musicVolume, _musicVolumeValue);
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
            Text = "Volume settings apply immediately and are saved locally on this device.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });
        layout.AddChild(CreateVolumeRow("AudioSfxVolume", "Sound effects", _sfxVolume, _sfxVolumeValue, _applySfxPreference));
        layout.AddChild(CreateVolumeRow("AudioMusicVolume", "Music", _musicVolume, _musicVolumeValue, _applyMusicPreference));

        layout.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        var footer = new HBoxContainer();
        footer.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var reset = new Button { Name = "AudioResetDefaults", Text = "Reset to default" };
        reset.Pressed += ResetToDefault;
        footer.AddChild(reset);
        var close = new Button { Name = "AudioClose", Text = "Close" };
        close.Pressed += Hide;
        footer.AddChild(close);
        layout.AddChild(footer);
    }

    private HBoxContainer CreateVolumeRow(
        string sliderName,
        string labelText,
        HSlider slider,
        Label valueLabel,
        System.Action<float> applyPreference)
    {
        slider.Name = sliderName;
        valueLabel.Name = $"{sliderName}Value";
        slider.MinValue = 0f;
        slider.MaxValue = 1f;
        slider.Step = 0.01;
        slider.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        slider.ValueChanged += _ => ApplyPreference(slider, valueLabel, applyPreference);
        valueLabel.HorizontalAlignment = HorizontalAlignment.Right;
        valueLabel.CustomMinimumSize = new Vector2(64f, 0f);

        var row = new HBoxContainer();
        row.AddChild(new Label { Text = labelText, CustomMinimumSize = new Vector2(150f, 0f) });
        row.AddChild(slider);
        row.AddChild(valueLabel);
        return row;
    }

    private void ApplyPreference(HSlider slider, Label valueLabel, System.Action<float> applyPreference)
    {
        if (_refreshing)
            return;
        RefreshVolumeLabel(slider, valueLabel);
        applyPreference((float)slider.Value);
    }

    private void ResetToDefault()
    {
        SetSfxVolume(LegacyAudioSettings.DefaultSfxVolume);
        SetMusicVolume(LegacyAudioSettings.DefaultMusicVolume);
        ApplyPreference(_sfxVolume, _sfxVolumeValue, _applySfxPreference);
        ApplyPreference(_musicVolume, _musicVolumeValue, _applyMusicPreference);
    }

    private static void RefreshVolumeLabel(HSlider slider, Label valueLabel)
    {
        float volume = (float)slider.Value;
        valueLabel.Text = volume == 0f ? "Muted" : $"{volume:P0}";
    }
}
