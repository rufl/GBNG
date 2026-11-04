using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunWindowFocusSmoke()
    {
        int exitCode = 0;
        int originalMaxFps = Engine.MaxFps;
        try
        {
            await WaitForUiFrames(2);
            Window applicationWindow = GetWindow();
            applicationWindow.EmitSignal(Window.SignalName.FocusExited);
            if (Engine.MaxFps != 30)
                throw new System.InvalidOperationException($"Losing window focus set the frame limit to {Engine.MaxFps}, expected 30.");

            applicationWindow.EmitSignal(Window.SignalName.FocusEntered);
            if (Engine.MaxFps != 0)
                throw new System.InvalidOperationException($"Regaining window focus set the frame limit to {Engine.MaxFps}, expected unlimited (0).");

            GD.Print("WINDOW_FOCUS_SMOKE_OK unfocusedMaxFps=30 focusedMaxFps=0 focusSignals=true");
        }
        catch (System.Exception exception)
        {
            exitCode = 1;
            GD.PushError($"WINDOW_FOCUS_SMOKE_FAILED: {exception}");
        }
        finally
        {
            Engine.MaxFps = originalMaxFps;
            GetTree().Quit(exitCode);
        }
    }
}
