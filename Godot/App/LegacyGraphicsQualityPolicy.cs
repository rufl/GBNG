using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private enum GraphicsQuality
    {
        High = 0,
        Medium = 1,
        Low = 2
    }

    private GraphicsQuality _graphicsQuality = GraphicsQuality.High;
    private CompositorEffect? _motionBlurEffect;
    private bool _motionBlurReady;

    private void ApplyGraphicsQuality(GraphicsQuality quality)
    {
        if (GetNodeOrNull<WorldEnvironment>("WorldEnvironment")?.Environment is Godot.Environment environment)
            environment.SsaoEnabled = quality == GraphicsQuality.High;

        if (GetNodeOrNull<DirectionalLight3D>("Sun") is DirectionalLight3D sun)
        {
            switch (quality)
            {
                case GraphicsQuality.High:
                    sun.ShadowEnabled = true;
                    sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
                    sun.DirectionalShadowMaxDistance = 100f;
                    break;
                case GraphicsQuality.Medium:
                    sun.ShadowEnabled = true;
                    sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits;
                    sun.DirectionalShadowMaxDistance = 40f;
                    break;
                case GraphicsQuality.Low:
                    sun.ShadowEnabled = false;
                    sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal;
                    sun.DirectionalShadowMaxDistance = 40f;
                    break;
            }
        }

        GetViewport().AnisotropicFilteringLevel = quality == GraphicsQuality.Low
            ? Viewport.AnisotropicFiltering.Disabled
            : Viewport.AnisotropicFiltering.Anisotropy4X;
        GetViewport().TextureMipmapBias = quality == GraphicsQuality.Low ? 1f : 0f;

        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Enabled);
        if (_motionBlurEffect is not null)
        {
            bool motionBlurEnabled = quality == GraphicsQuality.High && _motionBlurReady;
            _motionBlurEffect.Enabled = motionBlurEnabled;
            _motionBlurEffect.NeedsMotionVectors = motionBlurEnabled;
        }

    }

    private void ConfigureMotionBlur(WorldEnvironment worldEnvironment)
    {
        if (OS.HasFeature("mobile")
            || ProjectSettings.GetSetting("rendering/renderer/rendering_method").AsString() != "forward_plus"
            || RenderingServer.GetRenderingDevice() is null)
            return;

        GDScript script = GD.Load<GDScript>("res://Godot/App/LegacyMotionBlurEffect.gd")
            ?? throw new System.InvalidOperationException("The native motion-blur compositor effect is missing.");
        _motionBlurEffect = script.New().AsGodotObject() as CompositorEffect
            ?? throw new System.InvalidOperationException("The native motion-blur compositor effect could not be instantiated.");
        _motionBlurReady = _motionBlurEffect.Call("is_ready").AsBool();

        var compositor = new Compositor();
        var effects = new Godot.Collections.Array<CompositorEffect>();
        effects.Add(_motionBlurEffect);
        compositor.CompositorEffects = effects;
        worldEnvironment.Compositor = compositor;
        if (!_motionBlurReady)
            GD.PushError("The desktop Forward+ motion-blur compositor could not initialize; High quality will continue without motion blur.");
    }

    private void SetGraphicsQuality(int qualityIndex)
    {
        if (qualityIndex < (int)GraphicsQuality.High || qualityIndex > (int)GraphicsQuality.Low)
            return;
        GraphicsQuality quality = (GraphicsQuality)qualityIndex;
        if (_graphicsQuality == quality)
            return;

        _graphicsQuality = quality;
        ApplyGraphicsQuality(quality);
        if (_controlsWindow is not null && IsInstanceValid(_controlsWindow))
            _controlsWindow.SetGraphicsQuality(qualityIndex);
        SaveEditorPreferences();
    }

    private static GraphicsQuality ReadGraphicsQuality(ConfigFile settings)
    {
        double value = settings.GetValue("display", "graphics_quality", (int)GraphicsQuality.High).AsDouble();
        if (!double.IsFinite(value) || value != System.Math.Truncate(value)
            || value < (int)GraphicsQuality.High || value > (int)GraphicsQuality.Low)
            return GraphicsQuality.High;
        return (GraphicsQuality)(int)value;
    }
}
