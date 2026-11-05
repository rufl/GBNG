using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunOneShotAnimationSmoke()
    {
        int exitCode = 0;
        WorldActorNode? actor = null;
        AnimationPlayer? smokePlayer = null;
        string originalLoopingAnimation = "";
        bool capturedOriginalLoopingAnimation = false;
        try
        {
            actor = _actors.FirstOrDefault(candidate => candidate.GetNodeOrNull<Node3D>("Renderable") is not null)
                ?? throw new InvalidOperationException("The animation smoke project has no actor with a renderable.");
            Node3D renderable = actor.GetNode<Node3D>("Renderable");
            originalLoopingAnimation = actor.State.LoopingAnimation;
            capturedOriginalLoopingAnimation = true;

            var library = new AnimationLibrary();
            library.AddAnimation("SmokeLoop", new Animation { Length = 0.12f });
            library.AddAnimation("SmokeDance", CreateSmokeAnimation(0.12f));
            smokePlayer = new AnimationPlayer { Name = "OneShotAnimationSmokePlayer" };
            smokePlayer.AddAnimationLibrary("", library);
            renderable.AddChild(smokePlayer);

            actor.SetLoopingAnimation("SmokeLoop");
            if (smokePlayer.CurrentAnimation != "SmokeLoop" || !smokePlayer.IsPlaying())
                throw new InvalidOperationException("The test actor did not start its configured looping animation.");
            if (smokePlayer.GetAnimation("SmokeLoop").LoopMode != Animation.LoopModeEnum.Linear)
                throw new InvalidOperationException("The configured looping animation did not loop independently of its imported clip settings.");
            if (!actor.PlayOneShotAnimation("SmokeLoop") || smokePlayer.CurrentAnimation != "SmokeLoop")
                throw new InvalidOperationException("Requesting the current looping animation must leave it playing as a loop.");
            if (!actor.PlayOneShotAnimation("SmokeDance") || smokePlayer.CurrentAnimation != "SmokeDance")
                throw new InvalidOperationException("The one-shot animation did not start.");

            await ToSignal(GetTree().CreateTimer(0.2d), SceneTreeTimer.SignalName.Timeout);
            if (smokePlayer.CurrentAnimation != "SmokeLoop" || !smokePlayer.IsPlaying())
                throw new InvalidOperationException("The one-shot animation did not return to the configured loop after one cycle.");

            actor.SetLoopingAnimation("");
            if (!actor.PlayOneShotAnimation("SmokeDance") || smokePlayer.CurrentAnimation != "SmokeDance")
                throw new InvalidOperationException("The one-shot animation did not start without a configured loop.");
            await ToSignal(GetTree().CreateTimer(0.2d), SceneTreeTimer.SignalName.Timeout);
            if (smokePlayer.IsPlaying())
                throw new InvalidOperationException("A one-shot animation continued playing when no loop was configured.");

            GD.Print("ONE_SHOT_ANIMATION_SMOKE_OK oneCycle=true returnsToLoop=true noLoopStops=true sameLoopIgnored=true");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ONE_SHOT_ANIMATION_SMOKE_FAILED: {exception}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(smokePlayer))
            {
                smokePlayer.Stop();
                smokePlayer.QueueFree();
            }
            if (capturedOriginalLoopingAnimation && GodotObject.IsInstanceValid(actor))
                actor.SetLoopingAnimation(originalLoopingAnimation);
            GetTree().Quit(exitCode);
        }
    }

    private static Animation CreateSmokeAnimation(float length)
        => new() { Length = length, LoopMode = Animation.LoopModeEnum.Linear };
}
