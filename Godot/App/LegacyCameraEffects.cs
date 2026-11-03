using System;
using System.Collections.Generic;
using Godot;

namespace GBNG.App;

public sealed partial class LegacyCameraEffects : Node
{
    private static readonly string[] EffectIds = ["pixelate", "film", "bw", "saturate", "vhs", "krazy", "drawing"];
    private readonly HashSet<string> _enabledEffects = new(StringComparer.Ordinal);
    private ShaderMaterial _material = null!;
    private ColorRect _overlay = null!;

    internal static IReadOnlyList<string> AvailableEffectIds => EffectIds;

    public override void _Ready()
    {
        Shader shader = GD.Load<Shader>("res://Godot/App/LegacyCameraEffects.gdshader")
            ?? throw new InvalidOperationException("The native camera-effects shader is missing.");
        _material = new ShaderMaterial { Shader = shader };

        var canvasLayer = new CanvasLayer { Name = "CameraEffectsLayer", Layer = 5 };
        AddChild(canvasLayer);
        _overlay = new ColorRect
        {
            Name = "CameraEffectsOverlay",
            AnchorRight = 1f,
            AnchorBottom = 1f,
            Color = Colors.White,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Material = _material,
            Visible = false
        };
        canvasLayer.AddChild(_overlay);
    }

    public bool IsEffectEnabled(string effectId) => _enabledEffects.Contains(effectId);

    public void SetEffectEnabled(string effectId, bool enabled)
    {
        if (Array.IndexOf(EffectIds, effectId) < 0)
            throw new ArgumentException($"Unknown legacy camera effect '{effectId}'.", nameof(effectId));
        if (enabled)
            _enabledEffects.Add(effectId);
        else
            _enabledEffects.Remove(effectId);
        _material.SetShaderParameter($"fx_{effectId}", enabled);
        _overlay.Visible = _enabledEffects.Count > 0;
    }
}

public sealed partial class LegacyVisualEffectsWindow : Window
{
    internal static readonly (string Id, string Label)[] Effects =
    [
        ("pixelate", "Pixelate"),
        ("film", "Film"),
        ("bw", "B&W"),
        ("saturate", "Saturate"),
        ("vhs", "VHS"),
        ("krazy", "Krazy"),
        ("drawing", "Drawing")
    ];

    private readonly LegacyCameraEffects _effects;

    public LegacyVisualEffectsWindow(LegacyCameraEffects effects)
    {
        _effects = effects;
        Name = "VisualEffectsWindow";
        Title = "Visual Effects";
        MinSize = new Vector2I(360, 390);
        Size = new Vector2I(380, 420);
        Unresizable = true;
        Visible = false;

        var margin = new MarginContainer { AnchorRight = 1f, AnchorBottom = 1f };
        margin.AddThemeConstantOverride("margin_left", 18);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        AddChild(margin);

        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 8);
        margin.AddChild(content);
        content.AddChild(new Label
        {
            Text = "Temporary camera filters. These settings do not change the project.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        });

        foreach ((string id, string label) in Effects)
        {
            var toggle = new CheckBox
            {
                Name = $"VisualEffect_{id}",
                Text = label,
                ButtonPressed = _effects.IsEffectEnabled(id)
            };
            toggle.Toggled += enabled => _effects.SetEffectEnabled(id, enabled);
            content.AddChild(toggle);
        }

        var spacer = new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        content.AddChild(spacer);
        var close = new Button { Name = "VisualEffectsClose", Text = "Close", CustomMinimumSize = new Vector2(0f, 36f) };
        close.Pressed += Hide;
        content.AddChild(close);
    }
}
