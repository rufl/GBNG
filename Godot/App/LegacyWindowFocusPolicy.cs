using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private const int UnfocusedMaxFps = 30;

    private void InitializeApplicationFocusHandling()
    {
        _maxFpsBeforeFocusHandling = Engine.MaxFps;
        _applicationWindow = GetWindow();
        _applicationWindow.FocusEntered += OnApplicationFocusEntered;
        _applicationWindow.FocusExited += OnApplicationFocusExited;
        ApplyApplicationFocus(_applicationWindow.HasFocus());
    }

    private void OnApplicationFocusEntered() => ApplyApplicationFocus(isFocused: true);

    private void OnApplicationFocusExited() => ApplyApplicationFocus(isFocused: false);

    private static void ApplyApplicationFocus(bool isFocused)
    {
        Engine.MaxFps = isFocused ? 0 : UnfocusedMaxFps;
    }

    private void CleanupApplicationFocusHandling()
    {
        if (_applicationWindow is { } window && IsInstanceValid(window))
        {
            window.FocusEntered -= OnApplicationFocusEntered;
            window.FocusExited -= OnApplicationFocusExited;
        }

        _applicationWindow = null;
        Engine.MaxFps = _maxFpsBeforeFocusHandling;
    }
}
