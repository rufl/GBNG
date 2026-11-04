using Godot;
using GBNG.Domain;
using GBNG.World;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private const int DefaultActionPromptSmokePort = 57_321;

    private async void RunActionPromptLanHostSmoke()
    {
        int exitCode = 0;
        LegacyBehaviorAuthoringWindow? editor = null;
        try
        {
            await WaitForUiFrames(2);
            LegacySaveDocument document = _document ?? throw new InvalidOperationException("The LAN action-prompt smoke project did not load.");
            WorldActorNode? remotePlayer = null;
            int playerSlot = 0;
            foreach (WorldActorNode actor in _actors)
            {
                if (actor.State.IsPlayerControllable && ++playerSlot == 2)
                {
                    remotePlayer = actor;
                    break;
                }
            }
            if (remotePlayer is null)
                throw new InvalidOperationException("The LAN action-prompt smoke project has no second controllable player.");

            editor = new LegacyBehaviorAuthoringWindow(document, remotePlayer.State, OnBehaviorDataChanged, _ => null, ValidateBehaviorSourceForEditor, _catalog);
            AddChild(editor);
            editor.PopupCentered(new Vector2I(1000, 700));
            await WaitForUiFrames(2);
            InvokeSmoke(editor, "CreateCustomBehavior");
            var behaviorCode = (LegacyJavaScriptCodeEdit)editor.FindChild("BehaviorJavaScript", true, false);
            behaviorCode.Text = "export function OnTick(api) {\n"
                + "  api.addPlayerToolTip(api.name, 'action1', 'Remote action');\n"
                + "  api.addPlayerToolTip(api.name, 'action2', 'Remote secondary');\n"
                + "}";
            InvokeSmoke(editor, "ApplyScript");
            editor.QueueFree();
            editor = null;
            await WaitForUiFrames(2);

            bool clientReady = false;
            _lanSession.ClientProjectReadyReceived += _ => clientReady = true;
            int port = ReadActionPromptSmokePort();
            string hostPath = PrepareNetworkHostProjectCopy()
                ?? throw new InvalidOperationException("The LAN action-prompt smoke project could not be prepared for hosting.");
            Error hostError = _lanSession.Host(hostPath, "Action prompt smoke", port, "Host");
            if (hostError != Error.Ok)
                throw new InvalidOperationException($"The LAN action-prompt smoke host could not start on port {port}: {_lanSession.Status}");
            SetPlayMode(true);

            ulong deadline = Time.GetTicksMsec() + 45_000UL;
            while (!clientReady && Time.GetTicksMsec() < deadline)
                await WaitForUiFrames(1);
            if (!clientReady)
                throw new TimeoutException("The LAN action-prompt smoke client did not finish loading the hosted project.");
            await WaitForUiFrames(180);
            GD.Print($"ACTION_PROMPTS_LAN_HOST_OK port={port} remotePlayer={remotePlayer.LegacyName} clientReady=true");
        }
        catch (System.Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ACTION_PROMPTS_LAN_HOST_FAILED: {exception}");
        }
        finally
        {
            if (editor is not null && IsInstanceValid(editor))
                editor.QueueFree();
            if (_playMode)
                SetPlayMode(false);
            _lanSession.Leave();
            GetTree().Quit(exitCode);
        }
    }

    private async void RunActionPromptLanClientSmoke()
    {
        int exitCode = 0;
        try
        {
            await WaitForUiFrames(2);
            string? receivedPromptsJson = null;
            _lanSession.PlayerToolTipsReceived += prompts => receivedPromptsJson = prompts.ToJsonString();
            int port = ReadActionPromptSmokePort();
            Error joinError = _lanSession.Join("127.0.0.1", port, "Client");
            if (joinError != Error.Ok)
                throw new InvalidOperationException($"The LAN action-prompt smoke client could not join port {port}: {_lanSession.Status}");

            var action1Panel = (PanelContainer)_actionPrompt.FindChild("Action1Prompt", true, false);
            var action2Panel = (PanelContainer)_actionPrompt.FindChild("Action2Prompt", true, false);
            var action1Text = (Label)_actionPrompt.FindChild("Action1PromptText", true, false);
            var action2Text = (Label)_actionPrompt.FindChild("Action2PromptText", true, false);
            ulong deadline = Time.GetTicksMsec() + 45_000UL;
            while (Time.GetTicksMsec() < deadline
                && (!_playMode || !_actionPrompt.Visible || !action1Panel.Visible || !action2Panel.Visible
                    || action1Text.Text != "Remote action" || action2Text.Text != "Remote secondary"))
                await WaitForUiFrames(1);
            if (!_playMode || !_actionPrompt.Visible || !action1Panel.Visible || !action2Panel.Visible
                || action1Text.Text != "Remote action" || action2Text.Text != "Remote secondary")
                throw new TimeoutException(
                    $"The client did not display its host-authored Action1/Action2 prompts: playMode={_playMode}, "
                    + $"promptVisible={_actionPrompt.Visible}, action1Visible={action1Panel.Visible}, "
                    + $"action1='{action1Text.Text}', action2Visible={action2Panel.Visible}, action2='{action2Text.Text}', "
                    + $"localPlayer='{GetPlayerActor()?.LegacyName}', received={receivedPromptsJson ?? "<none>"}.");
            await WaitForUiFrames(2);

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/action-prompts-lan-client-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the LAN client action-prompt screenshot.");
            GD.Print($"ACTION_PROMPTS_LAN_CLIENT_OK localPlayer={GetPlayerActor()?.LegacyName} action1=true action2=true screenshot={screenshot}");
        }
        catch (System.Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ACTION_PROMPTS_LAN_CLIENT_FAILED: {exception}");
        }
        finally
        {
            if (_playMode)
                SetPlayMode(false, fromHost: true);
            _lanSession.Leave();
            GetTree().Quit(exitCode);
        }
    }

    private static int ReadActionPromptSmokePort()
    {
        const string prefix = "--smoke-action-prompts-port=";
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(argument.AsSpan(prefix.Length), out int port)
                && port is >= 1 and <= 65_535)
                return port;
        }
        return DefaultActionPromptSmokePort;
    }
}
