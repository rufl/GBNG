using System;
using System.Collections.Generic;
using System.Linq;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunActorRotateSmoke()
    {
        int exitCode = 0;
        bool originalRotateToolActive = _actorRotateToolActive;
        bool originalRotateSnap = _actorRotateSnap;
        bool originalLocalSpace = _actorMoveLocalSpace;
        bool originalAutoSetSpawn = _autoSetSpawnOnRotate;
        Dictionary<string, ActorMoveSnapshot>? originalActorStates = null;
        try
        {
            await WaitForUiFrames(3);
            if (_document is null || _welcome.Visible || !_editor.Visible)
                throw new InvalidOperationException("The actor-rotate smoke requires an open project in the editor.");
            if (!_actorRotateToolToggle.IsVisibleInTree()
                || !_actorRotateToolToggle.GetGlobalRect().Intersects(GetViewport().GetVisibleRect())
                || !_actorRotateSnapToggle.IsVisibleInTree()
                || !_actorRotateSnapToggle.GetGlobalRect().Intersects(GetViewport().GetVisibleRect()))
                throw new InvalidOperationException("The rotation tool and snap controls are not visible in the editor.");

            originalActorStates = CaptureActorMoveStates(_actors);
            int historyAtStart = _historyPosition;
            WorldActorNode? actor = _actors.FirstOrDefault(candidate =>
            {
                if (candidate.IsOffstageEffective || _camera.ToLocal(candidate.GlobalPosition).Z >= 0f)
                    return false;
                Vector2 screenPosition = _camera.UnprojectPosition(candidate.GetWorldRenderBounds().GetCenter());
                return !IsPointerOverUi(screenPosition)
                    && GetActorAtScreenPosition(screenPosition) == candidate;
            });
            if (actor is null)
                throw new InvalidOperationException("The sample has no visible actor available for the rotation smoke.");

            WorldActorNode? groupActor = _actors.FirstOrDefault(candidate =>
            {
                if (candidate == actor || candidate.IsOffstageEffective
                    || actor.IsAncestorOf(candidate) || candidate.IsAncestorOf(actor)
                    || _camera.ToLocal(candidate.GlobalPosition).Z >= 0f)
                    return false;
                Vector2 screenPosition = _camera.UnprojectPosition(candidate.GetWorldRenderBounds().GetCenter());
                return !IsPointerOverUi(screenPosition)
                    && GetActorAtScreenPosition(screenPosition) == candidate;
            });
            if (groupActor is null)
                throw new InvalidOperationException("The sample has no second visible independent actor for group rotation.");

            SetActorMoveLocalSpacePreference(false);
            SetActorRotateSnapPreference(false);
            SetAutoSetSpawnOnRotatePreference(true);
            SetActorSelection(new[] { actor }, actor);
            SetActorRotateToolPreference(true);
            UpdateActorRotateGizmo();
            int visibleRingIndex = FindActorRotateSmokeRingPoint(
                LegacyActorMoveGizmoAxis.Y,
                out Vector2 visibleRingPoint);
            if (!_actorRotateGizmo.Visible
                || visibleRingIndex < 0
                || !_actorRotateGizmo.TryPickAxis(visibleRingPoint, out LegacyActorMoveGizmoAxis visibleAxis)
                || visibleAxis != LegacyActorMoveGizmoAxis.Y)
                throw new InvalidOperationException("The rotation tool did not expose a pickable world Y ring.");

            Transform3D originalTransform = actor.GlobalTransform;
            Transform3D identityRotation = originalTransform;
            UpdateActorRotateGizmo();
            int singleStartIndex = FindActorRotateSmokeRingPoint(LegacyActorMoveGizmoAxis.Y, out Vector2 singleStart);
            SetActorRotateSnapPreference(true);
            using var savedSnapPreferences = new ConfigFile();
            if (savedSnapPreferences.Load(EditorSettingsPath) != Error.Ok
                || !savedSnapPreferences.GetValue("editor", "rotate_snap_to_legacy_orientations", false).AsBool())
                throw new InvalidOperationException("The rotation snap preference was not persisted.");

            int historyBeforeSingle = _historyPosition;
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = singleStart
            });
            if (!_actorRotatePending || _actorRotateConstraintAxis != LegacyActorMoveGizmoAxis.Y)
                throw new InvalidOperationException("Clicking the world Y ring did not begin a constrained rotation.");
            Vector2 singleTarget = _actorRotateGizmo.GetAxisScreenPoint(
                LegacyActorMoveGizmoAxis.Y,
                singleStartIndex + 8);
            _Input(new InputEventMouseMotion { Position = singleTarget });
            bool singleDragStarted = _actorRotateDragging;
            float singleDragRadians = _actorRotateAccumulatedRadians;
            Transform3D singleTransformAfterMotion = actor.GlobalTransform;
            _Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = singleTarget });
            Quaternion snappedExpected = new(Vector3.Up, Mathf.DegToRad(45f));
            Quaternion snappedActual = actor.GlobalBasis.Orthonormalized().GetRotationQuaternion();
            if (Mathf.Abs(snappedActual.Dot(snappedExpected)) < 0.9999f
                || !_actorRotateSnap
                || _historyPosition != historyBeforeSingle + 1
                || !actor.State.SpawnPosition.IsEqualApprox(actor.GlobalPosition)
                || !actor.State.SpawnRotation.IsEqualApprox(snappedActual))
                throw new InvalidOperationException(
                    $"Single-actor rotation or legacy snapping failed: actual={snappedActual}, expected={snappedExpected}, "
                    + $"dragging={singleDragStarted}, angle={singleDragRadians:0.000}, start={originalTransform}, "
                    + $"transformAfterMotion={singleTransformAfterMotion}, history={_historyPosition}/{historyBeforeSingle + 1}.");
            Transform3D snappedAfter = actor.GlobalTransform;
            Vector3 snappedSpawnPosition = actor.State.SpawnPosition;
            Quaternion snappedSpawnRotation = actor.State.SpawnRotation;
            UndoEdit();
            if (!actor.GlobalTransform.IsEqualApprox(identityRotation)
                || !actor.State.SpawnPosition.IsEqualApprox(originalActorStates[actor.LegacyName].SpawnPosition)
                || !actor.State.SpawnRotation.IsEqualApprox(originalActorStates[actor.LegacyName].SpawnRotation))
                throw new InvalidOperationException("Undo did not restore the single-actor rotation and restart state.");
            RedoEdit();
            if (!actor.GlobalTransform.IsEqualApprox(snappedAfter)
                || !actor.State.SpawnPosition.IsEqualApprox(snappedSpawnPosition)
                || !actor.State.SpawnRotation.IsEqualApprox(snappedSpawnRotation))
                throw new InvalidOperationException("Redo did not restore the snapped actor rotation and restart state.");
            UndoEdit();

            SetActorRotateSnapPreference(false);
            SetActorSelection(new[] { actor, groupActor }, actor);
            Transform3D groupActorBefore = actor.GlobalTransform;
            Transform3D groupPeerBefore = groupActor.GlobalTransform;
            Vector3 groupPivot = (groupActorBefore.Origin + groupPeerBefore.Origin) * 0.5f;
            UpdateActorRotateGizmo();
            int groupStartIndex = FindActorRotateSmokeRingPoint(LegacyActorMoveGizmoAxis.Y, out Vector2 groupStart);
            int historyBeforeGroup = _historyPosition;
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = groupStart
            });
            if (!_actorRotatePending || _actorRotateConstraintAxis != LegacyActorMoveGizmoAxis.Y)
                throw new InvalidOperationException("The group rotation did not start on the world Y ring.");
            Vector2 groupTarget = _actorRotateGizmo.GetAxisScreenPoint(
                LegacyActorMoveGizmoAxis.Y,
                groupStartIndex + 9);
            _Input(new InputEventMouseMotion { Position = groupTarget });
            _Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = groupTarget });
            Quaternion groupDelta = new(Vector3.Up, Mathf.DegToRad(45f));
            Transform3D groupActorAfter = actor.GlobalTransform;
            Transform3D groupPeerAfter = groupActor.GlobalTransform;
            if (groupActorAfter.Origin.DistanceTo(groupPivot + groupDelta * (groupActorBefore.Origin - groupPivot)) > 0.002f
                || groupPeerAfter.Origin.DistanceTo(groupPivot + groupDelta * (groupPeerBefore.Origin - groupPivot)) > 0.002f
                || _historyPosition != historyBeforeGroup + 1
                || !actor.State.SpawnPosition.IsEqualApprox(actor.GlobalPosition)
                || !groupActor.State.SpawnPosition.IsEqualApprox(groupActor.GlobalPosition))
                throw new InvalidOperationException("World-axis rotation did not rotate the selected group around its average pivot or update restart positions.");
            UndoEdit();
            if (!actor.GlobalTransform.IsEqualApprox(groupActorBefore)
                || !groupActor.GlobalTransform.IsEqualApprox(groupPeerBefore))
                throw new InvalidOperationException("Undo did not restore the multi-selection rotation.");
            RedoEdit();
            if (!actor.GlobalTransform.IsEqualApprox(groupActorAfter)
                || !groupActor.GlobalTransform.IsEqualApprox(groupPeerAfter))
                throw new InvalidOperationException("Redo did not restore the multi-selection rotation.");
            UndoEdit();

            SetActorSelection(new[] { actor }, actor);
            SetActorMoveLocalSpacePreference(true);
            Transform3D localStartTransform = actor.GlobalTransform;
            Quaternion localTilt = new(Vector3.Up, Mathf.DegToRad(35f));
            actor.GlobalTransform = new Transform3D(
                new Basis(localTilt),
                localStartTransform.Origin);
            actor.SaveTransform();
            localStartTransform = actor.GlobalTransform;
            UpdateActorRotateGizmo();
            Vector3 localXAxis = actor.GlobalBasis.Orthonormalized().X.Normalized();
            if (Mathf.Abs(localXAxis.Dot(Vector3.Right)) > 0.99f)
                throw new InvalidOperationException("The sample actor did not retain its local rotation for the local-axis smoke.");
            using var savedLocalPreferences = new ConfigFile();
            if (savedLocalPreferences.Load(EditorSettingsPath) != Error.Ok
                || !savedLocalPreferences.GetValue("editor", "move_local_space", false).AsBool())
                throw new InvalidOperationException("The local-axis preference did not persist for rotation handles.");

            int localStartIndex = FindActorRotateSmokeRingPoint(LegacyActorMoveGizmoAxis.X, out Vector2 localStart);
            int historyBeforeLocal = _historyPosition;
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = localStart
            });
            if (!_actorRotatePending
                || _actorRotateConstraintAxis != LegacyActorMoveGizmoAxis.X
                || !_actorRotateUsesLocalSpace
                || _actorRotateAxisDirection.DistanceTo(localXAxis) > 0.001f)
                throw new InvalidOperationException("Clicking the local X ring did not begin a local-axis rotation.");
            Vector2 localTarget = _actorRotateGizmo.GetAxisScreenPoint(
                LegacyActorMoveGizmoAxis.X,
                localStartIndex + 8);
            _Input(new InputEventMouseMotion { Position = localTarget });
            _Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = localTarget });
            Quaternion expectedLocalRotation = new Quaternion(localXAxis, Mathf.DegToRad(40f))
                * localStartTransform.Basis.GetRotationQuaternion();
            Transform3D localAfter = actor.GlobalTransform;
            if (Mathf.Abs(actor.GlobalBasis.GetRotationQuaternion().Dot(expectedLocalRotation)) < 0.9999f
                || actor.GlobalPosition.DistanceTo(localStartTransform.Origin) > 0.001f
                || _historyPosition != historyBeforeLocal + 1)
                throw new InvalidOperationException("Local X rotation did not apply the selected actor's local-axis angle.");
            UndoEdit();
            if (!actor.GlobalTransform.IsEqualApprox(localStartTransform))
                throw new InvalidOperationException("Undo did not restore the local-axis rotation.");
            RedoEdit();
            if (!actor.GlobalTransform.IsEqualApprox(localAfter))
                throw new InvalidOperationException("Redo did not restore the local-axis rotation.");
            UndoEdit();

            Transform3D beforeCancel = actor.GlobalTransform;
            Vector3 spawnBeforeCancel = actor.State.SpawnPosition;
            Quaternion spawnRotationBeforeCancel = actor.State.SpawnRotation;
            int historyBeforeCancel = _historyPosition;
            UpdateActorRotateGizmo();
            int cancelStartIndex = FindActorRotateSmokeRingPoint(LegacyActorMoveGizmoAxis.X, out Vector2 cancelStart);
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = cancelStart
            });
            Vector2 cancelTarget = _actorRotateGizmo.GetAxisScreenPoint(
                LegacyActorMoveGizmoAxis.X,
                cancelStartIndex + 8);
            _Input(new InputEventMouseMotion { Position = cancelTarget });
            _Input(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            if (!actor.GlobalTransform.IsEqualApprox(beforeCancel)
                || !actor.State.SpawnPosition.IsEqualApprox(spawnBeforeCancel)
                || !actor.State.SpawnRotation.IsEqualApprox(spawnRotationBeforeCancel)
                || _historyPosition != historyBeforeCancel)
                throw new InvalidOperationException("Escape did not restore the in-progress rotation without creating history.");

            ApplyActorMoveStates(originalActorStates);
            SetActorSelection(new[] { actor }, actor);
            SetActorMoveLocalSpacePreference(originalLocalSpace);
            SetActorRotateSnapPreference(originalRotateSnap);
            SetAutoSetSpawnOnRotatePreference(originalAutoSetSpawn);
            SetActorRotateToolPreference(true);
            UpdateActorRotateGizmo();
            if (_historyPosition != historyAtStart)
                throw new InvalidOperationException($"Rotation smoke left history at {_historyPosition}; expected {historyAtStart}.");
            WorldActorNode? screenshotActor = _actors.FirstOrDefault(candidate =>
            {
                if (candidate.IsOffstageEffective || _camera.ToLocal(candidate.GlobalPosition).Z >= 0f)
                    return false;
                Vector2 screenPosition = _camera.UnprojectPosition(candidate.GetWorldRenderBounds().GetCenter());
                return GetViewport().GetVisibleRect().HasPoint(screenPosition)
                    && screenPosition.X > GetViewport().GetVisibleRect().Size.X * 0.5f
                    && screenPosition.Y < GetViewport().GetVisibleRect().Size.Y * 0.5f
                    && !IsPointerOverUi(screenPosition)
                    && GetActorAtScreenPosition(screenPosition) == candidate;
            });
            if (screenshotActor is not null)
                SetActorSelection(new[] { screenshotActor }, screenshotActor);
            UpdateActorRotateGizmo();

            if (!_actorRotateToolToggle.ButtonPressed || !_actorRotateGizmo.Visible)
                throw new InvalidOperationException(
                    $"The rotation rings were not visible after selecting a screenshot actor: active={_actorRotateToolActive}, "
                    + $"toggle={_actorRotateToolToggle.ButtonPressed}, selected={_selectedActor?.LegacyName ?? string.Empty}, "
                    + $"selectedCount={_selectedActors.Count}, screen={(_selectedActor is null ? Vector2.Zero : _camera.UnprojectPosition(_selectedActor.GlobalPosition))}, "
                    + $"welcome={_welcome.Visible}, play={_playMode}.");
            await WaitForUiFrames(2);
            string screenshotPath = ProjectSettings.GlobalizePath("res://.godot/actor-rotate-smoke.png");
            using Image screenshot = GetViewport().GetTexture().GetImage();
            if (screenshot.SavePng(screenshotPath) != Error.Ok)
                throw new InvalidOperationException("Could not write the actor-rotate smoke screenshot.");
            GD.Print($"ACTOR_ROTATE_SMOKE_OK rings=world+local snap=legacy-orientations groupPivot=true restartTransforms=true undoRedo=true cancel=true screenshot={screenshotPath}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ACTOR_ROTATE_SMOKE_FAILED: {exception}");
        }
        finally
        {
            if (originalActorStates is not null)
                ApplyActorMoveStates(originalActorStates);
            SetActorRotateToolPreference(originalRotateToolActive);
            SetActorMoveLocalSpacePreference(originalLocalSpace);
            SetActorRotateSnapPreference(originalRotateSnap);
            SetAutoSetSpawnOnRotatePreference(originalAutoSetSpawn);
            if (_actorRotateGizmo is not null && IsInstanceValid(_actorRotateGizmo))
                _actorRotateGizmo.Free();
        }

        GetTree().Quit(exitCode);
    }

    private int FindActorRotateSmokeRingPoint(LegacyActorMoveGizmoAxis axis, out Vector2 screenPosition)
    {
        for (int index = 0; index < LegacyActorRotateGizmoControl.RingSegments; index++)
        {
            Vector2 candidate = _actorRotateGizmo.GetAxisScreenPoint(axis, index);
            if (IsPointerOverUi(candidate)
                || !GetViewport().GetVisibleRect().HasPoint(candidate)
                || !_actorRotateGizmo.TryPickAxis(candidate, out LegacyActorMoveGizmoAxis pickedAxis)
                || pickedAxis != axis)
                continue;
            screenPosition = candidate;
            return index;
        }

        throw new InvalidOperationException($"Could not find a visible, unobstructed {axis} rotation ring point.");
    }
}
