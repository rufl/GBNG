using Godot;
using GBNG.Domain;
using GBNG.World;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunActionPromptSmoke()
    {
        int exitCode = 0;
        LegacyBehaviorAuthoringWindow? behaviorEditor = null;
        try
        {
            await WaitForUiFrames(2);
            LegacySaveDocument document = _document ?? throw new InvalidOperationException("The action-prompt smoke project did not load.");
            WorldActorNode player = GetPlayerActor() ?? throw new InvalidOperationException("The action-prompt smoke project has no controllable player actor.");
            behaviorEditor = new LegacyBehaviorAuthoringWindow(
                document,
                player.State,
                OnBehaviorDataChanged,
                _ => null,
                ValidateBehaviorSourceForEditor,
                _catalog);
            AddChild(behaviorEditor);
            behaviorEditor.PopupCentered(new Vector2I(1000, 700));
            await WaitForUiFrames(2);
            InvokeSmoke(behaviorEditor, "CreateCustomBehavior");

            var behaviorCode = (LegacyJavaScriptCodeEdit)behaviorEditor.FindChild("BehaviorJavaScript", true, false);
            const string promptSource = "export function OnTick(api) {\n"
                + "  api.addPlayerToolTip(api.name, 'action1', 'Use');\n"
                + "  api.addPlayerToolTip(api.name, 'action2', 'Grab');\n"
                + "  api.addPlayerToolTip(api.name, 'unsupported', 'Ignore this action');\n"
                + "  api.addPlayerToolTip('another-player', 'action1', 'Ignore this player');\n"
                + "}";
            behaviorCode.Text = promptSource;
            InvokeSmoke(behaviorEditor, "ApplyScript");
            behaviorEditor.QueueFree();
            behaviorEditor = null;
            await WaitForUiFrames(2);

            SetPlayMode(false);
            _behaviorRuntime?.Tick(1d / 60d);
            var action1Panel = (PanelContainer)_actionPrompt.FindChild("Action1Prompt", true, false);
            var action2Panel = (PanelContainer)_actionPrompt.FindChild("Action2Prompt", true, false);
            var action1Binding = (Label)_actionPrompt.FindChild("Action1PromptBinding", true, false);
            var action2Binding = (Label)_actionPrompt.FindChild("Action2PromptBinding", true, false);
            var action1Text = (Label)_actionPrompt.FindChild("Action1PromptText", true, false);
            var action2Text = (Label)_actionPrompt.FindChild("Action2PromptText", true, false);
            if (_actionPrompt.Visible || action1Panel.Visible || action2Panel.Visible)
                throw new InvalidOperationException("The action prompts appeared before play mode began.");
            SetPlayMode(true);
            await WaitForUiFrames(1);
            if (!_actionPrompt.Visible
                || !action1Panel.Visible
                || !action2Panel.Visible
                || action1Binding.Text != "LMB"
                || action2Binding.Text != "RMB"
                || action1Text.Text != "Use"
                || action2Text.Text != "Grab")
                throw new InvalidOperationException("The behavior runtime did not display player-specific Action1/Action2 prompts.");

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/action-prompts-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the action-prompt screenshot.");

            SetPlayMode(false);
            if (_actionPrompt.Visible || action1Panel.Visible || action2Panel.Visible)
                throw new InvalidOperationException("Leaving play mode did not clear the player action prompts.");
            GD.Print($"ACTION_PROMPTS_SMOKE_OK behaviorApi=true playerFilter=true action1=true action2=true playExitClears=true screenshot={screenshot}");
        }
        catch (System.Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ACTION_PROMPTS_SMOKE_FAILED: {exception}");
        }
        finally
        {
            if (behaviorEditor is not null && IsInstanceValid(behaviorEditor))
                behaviorEditor.QueueFree();
            if (_playMode)
                SetPlayMode(false);
            GetTree().Quit(exitCode);
        }
    }
}
