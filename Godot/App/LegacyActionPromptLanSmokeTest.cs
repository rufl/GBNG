using Godot;
using GBNG.Domain;
using GBNG.World;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private const int DefaultActionPromptSmokePort = 57_321;
    private static readonly Color ActionPromptLanSnapshotTint = new(0.75f, 0.25f, 0.5f, 1f);

    private async void RunActionPromptLanHostSmoke()
    {
        int exitCode = 0;
        LegacyBehaviorAuthoringWindow? editor = null;
        try
        {
            LegacySaveDocument document = _document ?? throw new InvalidOperationException("The LAN action-prompt smoke project did not load.");
            WorldActorNode hostPlayer = GetPlayerActor()
                ?? throw new InvalidOperationException("The LAN action-prompt smoke project has no local host player.");
            WorldActorNode? transferTarget = _actors.FirstOrDefault(actor =>
                !actor.State.IsPlayerControllable
                && !actor.LegacyName.StartsWith("builtin:", StringComparison.Ordinal));
            if (transferTarget is null)
                throw new InvalidOperationException("The LAN action-prompt smoke project has no unassigned transfer target.");

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
            behaviorCode.Text = "let transferRequested = false;\n"
                + "export function OnTick(api) {\n"
                + "  api.addPlayerToolTip(api.name, 'action1', 'Remote action');\n"
                + "  api.addPlayerToolTip(api.name, 'action2', 'Remote secondary');\n"
                + "  if (!transferRequested) {\n"
                + "    transferRequested = true;\n"
                + "    callVoosService('TransferPlayerControl', { fromActor: "
                + System.Text.Json.JsonSerializer.Serialize(hostPlayer.LegacyName)
                + ", toActor: " + System.Text.Json.JsonSerializer.Serialize(transferTarget.LegacyName)
                + ", destroy: false });\n"
                + "  }\n"
                + "}";
            InvokeSmoke(editor, "ApplyScript");
            editor.QueueFree();
            editor = null;
            await WaitForUiFrames(2);

            long clientPeerId = 0;
            bool clientReady = false;
            _lanSession.ClientProjectReadyReceived += peerId =>
            {
                clientPeerId = peerId;
                clientReady = true;
            };
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
            ulong transferDeadline = Time.GetTicksMsec() + 10_000UL;
            while (GetPlayerActor()?.LegacyName != transferTarget.LegacyName
                && Time.GetTicksMsec() < transferDeadline)
                await WaitForUiFrames(1);
            if (GetPlayerActor()?.LegacyName != transferTarget.LegacyName)
                throw new TimeoutException("The host did not apply the scripted local control transfer.");
            await WaitForUiFrames(180);
            remotePlayer.SetTint(ActionPromptLanSnapshotTint);
            await WaitForUiFrames(30);
            ulong clientDeadline = Time.GetTicksMsec() + 45_000UL;
            while (clientPeerId > 1
                && _lanSession.Players.Any(player => player.Id == clientPeerId)
                && Time.GetTicksMsec() < clientDeadline)
                await WaitForUiFrames(1);
            if (clientPeerId <= 1 || _lanSession.Players.Any(player => player.Id == clientPeerId))
                throw new TimeoutException("The LAN action-prompt smoke client did not complete its prompt and snapshot checks.");
            GD.Print($"ACTION_PROMPTS_LAN_HOST_OK port={port} remotePlayer={remotePlayer.LegacyName} clientReady=true snapshotTint=true clientCompleted=true");
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
            WorldActorNode? expectedRemotePlayer = WorldActorNode.FindPlayerActorAtSlot(_actors, 2);
            WorldActorNode? expectedTransferTarget = _actors.FirstOrDefault(actor =>
                !actor.State.IsPlayerControllable
                && !actor.LegacyName.StartsWith("builtin:", StringComparison.Ordinal));
            if (expectedRemotePlayer is null || expectedTransferTarget is null)
                throw new InvalidOperationException("The LAN action-prompt smoke project has no actors for the slot-transfer check.");
            string expectedRemotePlayerName = expectedRemotePlayer.LegacyName;
            string expectedTransferTargetName = expectedTransferTarget.LegacyName;
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
                    || action1Text.Text != "Remote action" || action2Text.Text != "Remote secondary"
                    || _behaviorRuntime?.GetPlayerActorAtSlot(1)?.LegacyName != expectedTransferTargetName
                    || _behaviorRuntime?.GetPlayerActorAtSlot(2)?.LegacyName != expectedRemotePlayerName
                    || GetPlayerActor()?.State.Tint != ActionPromptLanSnapshotTint))
                await WaitForUiFrames(1);
            if (!_playMode || !_actionPrompt.Visible || !action1Panel.Visible || !action2Panel.Visible
                || action1Text.Text != "Remote action" || action2Text.Text != "Remote secondary"
                || _behaviorRuntime?.GetPlayerActorAtSlot(1)?.LegacyName != expectedTransferTargetName
                || _behaviorRuntime?.GetPlayerActorAtSlot(2)?.LegacyName != expectedRemotePlayerName
                || GetPlayerActor()?.State.Tint != ActionPromptLanSnapshotTint)
                throw new TimeoutException(
                    $"The client did not receive host prompts, control slots, and live snapshot: playMode={_playMode}, "
                    + $"isClient={_lanSession.IsClient}, status='{_lanSession.Status}', "
                    + $"promptVisible={_actionPrompt.Visible}, action1='{action1Text.Text}', action2='{action2Text.Text}', "
                    + $"controlSlot1={_behaviorRuntime?.GetPlayerActorAtSlot(1)?.LegacyName ?? "<none>"}, "
                    + $"controlSlot2={_behaviorRuntime?.GetPlayerActorAtSlot(2)?.LegacyName ?? "<none>"}, "
                    + $"expectedSlot1={expectedTransferTargetName}, expectedSlot2={expectedRemotePlayerName}, "
                    + $"playerTint={GetPlayerActor()?.State.Tint}, expectedTint={ActionPromptLanSnapshotTint}, "
                    + $"received={receivedPromptsJson ?? "<none>"}.");
            await WaitForUiFrames(2);

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/action-prompts-lan-client-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the LAN client action-prompt screenshot.");
            GD.Print(
                $"ACTION_PROMPTS_LAN_CLIENT_OK localPlayer={GetPlayerActor()?.LegacyName} "
                + $"controlSlot1={_behaviorRuntime?.GetPlayerActorAtSlot(1)?.LegacyName} "
                + $"controlSlot2={_behaviorRuntime?.GetPlayerActorAtSlot(2)?.LegacyName} "
                + $"action1=true action2=true snapshotTint=true screenshot={screenshot}");
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
