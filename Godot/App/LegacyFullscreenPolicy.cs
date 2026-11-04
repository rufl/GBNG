using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private void ApplyFullscreenMode(bool fullscreen)
    {
        Window window = GetWindow();
        if (IsFullscreenMode(window.Mode) == fullscreen)
            return;

        window.Mode = fullscreen
            ? Window.ModeEnum.ExclusiveFullscreen
            : Window.ModeEnum.Windowed;
    }

    private static bool IsFullscreenMode(Window.ModeEnum mode)
        => mode is Window.ModeEnum.Fullscreen or Window.ModeEnum.ExclusiveFullscreen;
}
