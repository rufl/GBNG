using System;
using System.Collections.Generic;
using System.Linq;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunActorScaleSmoke()
    {
        int exitCode = 0;
        bool originalScaleToolActive = _actorScaleToolActive;
        bool originalRotateToolActive = _actorRotateToolActive;
        Dictionary<string, ActorMoveSnapshot>? originalActorStates = null;
        try
        {
            await WaitForUiFrames(3);
            if (_document is null || _welcome.Visible || !_editor.Visible)
                throw new InvalidOperationException("The actor-scale smoke requires an open project in the editor.");
            if (!_actorScaleToolToggle.IsVisibleInTree()
                || !_actorScaleToolToggle.GetGlobalRect().Intersects(GetViewport().GetVisibleRect()))
                throw new InvalidOperationException("The scale gizmo control is not visible in the editor.");

            originalActorStates = CaptureActorMoveStates(_actors);
            int historyAtStart = _historyPosition;
            WorldActorNode? actor = _actors.FirstOrDefault(IsActorScaleSmokeCandidate);
            if (actor is null)
                throw new InvalidOperationException("The sample has no visible actor with a stable scale for the scale smoke.");
            WorldActorNode? groupActor = _actors.FirstOrDefault(candidate =>
                candidate != actor
                && !actor.IsAncestorOf(candidate)
                && !candidate.IsAncestorOf(actor)
                && IsActorScaleSmokeCandidate(candidate));

            SetActorSelection(groupActor is null ? new[] { actor } : new[] { actor, groupActor }, actor);
            SetActorRotateToolPreference(true);
            UpdateActorRotateGizmo();
            if (!_actorRotateToolToggle.ButtonPressed || !_actorRotateToolActive)
                throw new InvalidOperationException("The rotation tool did not activate before the scale gizmo.");
            SetActorScaleToolPreference(true);
            UpdateActorScaleGizmo();
            if (!_actorScaleToolToggle.ButtonPressed
                || _actorRotateToolToggle.ButtonPressed
                || _actorRotateToolActive
                || _actorRotateGizmo.Visible)
                throw new InvalidOperationException("Activating scale did not turn off the exclusive rotation tool.");
            Vector2 center = _camera.UnprojectPosition(actor.GetWorldRenderBounds().GetCenter());
            if (!_actorScaleGizmo.Visible || !_actorScaleGizmo.TryPick(center))
                throw new InvalidOperationException("The focused actor did not expose a pickable scale gizmo.");

            Transform3D originalTransform = actor.GlobalTransform;
            Vector3 originalScale = actor.Scale;
            Transform3D originalGroupTransform = groupActor?.GlobalTransform ?? Transform3D.Identity;
            Vector3 originalGroupScale = groupActor?.Scale ?? Vector3.One;
            int historyBeforeScale = _historyPosition;
            BeginActorScaleSmokeGesture(center);
            ApplyActorScaleSmokeMotion(center, new Vector2(10f, 0f), withModifier: false);
            Vector3 expectedScale = originalScale * (1f + 10f * ScaleDragSensitivity);
            Vector3 expectedGroupScale = originalGroupScale * (1f + 10f * ScaleDragSensitivity);
            if (!ScaleSmokeVectorMatches(actor.Scale, expectedScale)
                || (groupActor is not null && !ScaleSmokeVectorMatches(groupActor.Scale, expectedGroupScale)))
                throw new InvalidOperationException("A uniform drag did not multiply each selected actor's local scale by the legacy factor.");
            FinishActorScaleSmokeGesture(center + new Vector2(10f, 0f));
            if (_historyPosition != historyBeforeScale + 1)
                throw new InvalidOperationException("A completed scale drag did not create exactly one undo step.");
            AssertSpawnTransformMatchesCurrent(actor, "scaling an actor with the scale gizmo");
            if (groupActor is not null)
                AssertSpawnTransformMatchesCurrent(groupActor, "scaling a multi-selection with the scale gizmo");

            UndoEdit();
            if (!actor.GlobalTransform.IsEqualApprox(originalTransform)
                || (groupActor is not null && !groupActor.GlobalTransform.IsEqualApprox(originalGroupTransform)))
                throw new InvalidOperationException("Undo did not restore the selected actors' original scales and transforms.");
            RedoEdit();
            if (!ScaleSmokeVectorMatches(actor.Scale, expectedScale)
                || (groupActor is not null && !ScaleSmokeVectorMatches(groupActor.Scale, expectedGroupScale)))
                throw new InvalidOperationException("Redo did not restore the scale-gizmo result.");
            UndoEdit();

            center = _camera.UnprojectPosition(actor.GetWorldRenderBounds().GetCenter());
            BeginActorScaleSmokeGesture(center);
            ApplyActorScaleSmokeMotion(center, new Vector2(10f, 0f), withModifier: true);
            Vector3 expectedFastScale = originalScale * (1f + 10f * ScaleDragSensitivityWithModifier);
            if (!ScaleSmokeVectorMatches(actor.Scale, expectedFastScale))
                throw new InvalidOperationException("Ctrl/Cmd did not apply the legacy faster scale-drag sensitivity.");
            FinishActorScaleSmokeGesture(center + new Vector2(10f, 0f));
            UndoEdit();

            center = _camera.UnprojectPosition(actor.GetWorldRenderBounds().GetCenter());
            BeginActorScaleSmokeGesture(center);
            ApplyActorScaleSmokeMotion(center, new Vector2(-130f, 0f), withModifier: false);
            if (!ScaleSmokeVectorMatches(actor.Scale, Vector3.One * ScaleMinimum))
                throw new InvalidOperationException("Scaling below the legacy minimum did not clamp uniformly to 0.1.");
            FinishActorScaleSmokeGesture(center + new Vector2(-130f, 0f));
            UndoEdit();
            if (!actor.GlobalTransform.IsEqualApprox(originalTransform))
                throw new InvalidOperationException("Undo after minimum-scale clamping did not restore the original transform.");

            center = _camera.UnprojectPosition(actor.GetWorldRenderBounds().GetCenter());
            int historyBeforeCancel = _historyPosition;
            BeginActorScaleSmokeGesture(center);
            ApplyActorScaleSmokeMotion(center, new Vector2(10f, 0f), withModifier: false);
            if (actor.Scale.IsEqualApprox(originalScale))
                throw new InvalidOperationException("The scale-cancel check did not change the actor scale before Escape.");
            _Input(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            if (_actorScalePending
                || !actor.GlobalTransform.IsEqualApprox(originalTransform)
                || _historyPosition != historyBeforeCancel)
                throw new InvalidOperationException("Escape did not restore the in-progress scale without recording history.");

            if (_historyPosition != historyAtStart)
                throw new InvalidOperationException($"Scale smoke left history at {_historyPosition}; expected {historyAtStart}.");

            ApplyActorMoveStates(originalActorStates);
            WorldActorNode? screenshotActor = _actors.FirstOrDefault(candidate =>
            {
                if (!IsActorScaleSmokeCandidate(candidate))
                    return false;
                Vector2 screenPosition = _camera.UnprojectPosition(candidate.GetWorldRenderBounds().GetCenter());
                return screenPosition.X > GetViewport().GetVisibleRect().Size.X * 0.5f
                    && screenPosition.Y < GetViewport().GetVisibleRect().Size.Y * 0.5f;
            });
            if (screenshotActor is not null)
                SetActorSelection(new[] { screenshotActor }, screenshotActor);
            UpdateActorScaleGizmo();
            if (!_actorScaleToolToggle.ButtonPressed || !_actorScaleGizmo.Visible)
                throw new InvalidOperationException("The scale gizmo was not visible after selecting a screenshot actor.");
            await WaitForUiFrames(2);
            string screenshotPath = ProjectSettings.GlobalizePath("res://.godot/actor-scale-smoke.png");
            using Image screenshot = GetViewport().GetTexture().GetImage();
            if (screenshot.SavePng(screenshotPath) != Error.Ok)
                throw new InvalidOperationException("Could not write the actor-scale smoke screenshot.");
            GD.Print($"ACTOR_SCALE_SMOKE_OK uniform=true multiSelection={groupActor is not null} modifierFast=true minimum=0.1 restartTransforms=true undoRedo=true cancel=true screenshot={screenshotPath}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ACTOR_SCALE_SMOKE_FAILED: {exception}");
        }
        finally
        {
            if (_actorScalePending)
                CancelActorScale();
            if (originalActorStates is not null)
                ApplyActorMoveStates(originalActorStates);
            SetActorScaleToolPreference(false);
            SetActorRotateToolPreference(originalRotateToolActive);
            if (originalScaleToolActive && !originalRotateToolActive)
                SetActorScaleToolPreference(true);
            if (_actorScaleGizmo is not null && IsInstanceValid(_actorScaleGizmo))
                _actorScaleGizmo.Free();
        }

        GetTree().Quit(exitCode);
    }

    private bool IsActorScaleSmokeCandidate(WorldActorNode candidate)
    {
        if (candidate.IsOffstageEffective || _camera.ToLocal(candidate.GlobalPosition).Z >= 0f)
            return false;
        Vector3 scale = candidate.Scale;
        if (scale.X <= 0.2f || scale.X > 1.5f || scale.Y <= 0.2f || scale.Z <= 0.2f)
            return false;
        Vector2 screenPosition = _camera.UnprojectPosition(candidate.GetWorldRenderBounds().GetCenter());
        return GetViewport().GetVisibleRect().HasPoint(screenPosition)
            && !IsPointerOverUi(screenPosition)
            && GetActorAtScreenPosition(screenPosition) == candidate;
    }

    private void BeginActorScaleSmokeGesture(Vector2 center)
    {
        _UnhandledInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = center
        });
        if (!_actorScalePending)
            throw new InvalidOperationException("Clicking the scale gizmo did not begin scaling.");
    }

    private void ApplyActorScaleSmokeMotion(Vector2 center, Vector2 relative, bool withModifier)
    {
        _Input(new InputEventMouseMotion
        {
            Position = center + relative,
            Relative = relative,
            CtrlPressed = withModifier
        });
    }

    private void FinishActorScaleSmokeGesture(Vector2 position)
    {
        _Input(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
            Position = position
        });
        if (_actorScalePending)
            throw new InvalidOperationException("Releasing the scale drag did not finish the operation.");
    }

    private static bool ScaleSmokeVectorMatches(Vector3 actual, Vector3 expected)
        => actual.DistanceTo(expected) < 0.001f;
}
