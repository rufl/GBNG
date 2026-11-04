using Godot;
using GBNG.Audio;

namespace GBNG.App;

public sealed partial class LegacyAudioSettingsWindow : Window
{
    private readonly System.Action<float> _applyPreference;
    private readonly HSlider _sfxVolume = new();
    private readonly Label _sfxVolumeValue = new();
    private bool _refreshing;

    public LegacyAudioSettingsWindow(System.Action<float> applyPreference)
    {
        _applyPreference = applyPreference;
        Name = "AudioSettingsWindow";
        Title = "Audio";
        Size = new Vector2I(540, 230);
        Hide();
        CloseRequested += Hide;
        BuildInterface();
        SetSfxVolume(LegacyAudioSettings.DefaultSfxVolume);
    }

    public void SetSfxVolume(float volume)
    {
        _refreshing = true;
        _sfxVolume.Value = LegacyAudioSettings.ClampSfxVolume(volume);
        _refreshing = false;
        RefreshVolumeLabel();
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
            Text = "Sound effects volume. This device preference applies immediately and is saved locally.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });

        _sfxVolume.Name = "AudioSfxVolume";
        _sfxVolume.MinValue = 0f;
        _sfxVolume.MaxValue = 1f;
        _sfxVolume.Step = 0.01;
        _sfxVolume.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _sfxVolume.ValueChanged += _ => ApplyCurrentPreference();
        _sfxVolumeValue.Name = "AudioSfxVolumeValue";
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = "Sound effects", CustomMinimumSize = new Vector2(150f, 0f) });
        row.AddChild(_sfxVolume);
        _sfxVolumeValue.HorizontalAlignment = HorizontalAlignment.Right;
        _sfxVolumeValue.CustomMinimumSize = new Vector2(64f, 0f);
        row.AddChild(_sfxVolumeValue);
        layout.AddChild(row);

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

    private void ApplyCurrentPreference()
    {
        if (_refreshing)
            return;
        RefreshVolumeLabel();
        _applyPreference((float)_sfxVolume.Value);
    }

    private void ResetToDefault()
    {
        SetSfxVolume(LegacyAudioSettings.DefaultSfxVolume);
        ApplyCurrentPreference();
    }

    private void RefreshVolumeLabel()
    {
        float volume = (float)_sfxVolume.Value;
        _sfxVolumeValue.Text = volume == 0f ? "Muted" : $"{volume:P0}";
    }
}
