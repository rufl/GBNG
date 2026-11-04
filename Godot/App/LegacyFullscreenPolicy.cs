using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private void ApplyFullscreenMode(bool fullscreen)
    {
        Window window = GetWindow();
        if (IsFullscreenMode(window.Mode) == fullscreen)
        {
            RefreshControlsDisplayState();
            return;
        }

        if (fullscreen && !IsFullscreenMode(window.Mode))
            _windowedSize = ClampWindowedSize(window.Size, DisplayServer.ScreenGetUsableRect(window.CurrentScreen));
        _windowedSizeSaveTimer?.Stop();
        bool wasApplyingDisplayChange = _applyingDisplayChange;
        _applyingDisplayChange = true;
        try
        {
            window.Mode = fullscreen
                ? Window.ModeEnum.ExclusiveFullscreen
                : Window.ModeEnum.Windowed;
            if (!fullscreen)
            {
                _windowedSize = ClampWindowedSize(_windowedSize, DisplayServer.ScreenGetUsableRect(window.CurrentScreen));
                window.Size = _windowedSize;
                CenterWindow(window, _windowedSize);
            }
        }
        finally
        {
            _applyingDisplayChange = wasApplyingDisplayChange;
        }
        RefreshControlsDisplayState();
    }

    private static bool IsFullscreenMode(Window.ModeEnum mode)
        => mode is Window.ModeEnum.Fullscreen or Window.ModeEnum.ExclusiveFullscreen;
}
