using System;
using System.Collections.Generic;
using System.Linq;
using GBNG.Domain;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private sealed record ActorMoveSnapshot(
        Transform3D Transform,
        Vector3 Velocity,
        Vector3 AngularVelocity,
        Vector3 SpawnPosition,
        Quaternion SpawnRotation);

    private bool _actorMovePending;
    private bool _actorMoveDragging;
    private Vector2 _actorMoveStartPointer;
    private Vector3 _actorMovePlanePoint;
    private Vector3 _actorMovePlaneNormal;
    private Vector3 _actorMovePointerOffset;
    private string _actorMoveFocusName = "";
    private WorldActorNode[] _actorMoveSelectedActors = Array.Empty<WorldActorNode>();
    private WorldActorNode[] _actorMoveAffectedActors = Array.Empty<WorldActorNode>();
    private Dictionary<string, ActorMoveSnapshot>? _actorMoveBefore;

    private bool HandleActorMoveInput(InputEvent inputEvent)
    {
        if (!_actorMovePending)
            return false;

        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            FinishActorMove();
            GetViewport().SetInputAsHandled();
            return true;
        }

        if (inputEvent is InputEventMouseMotion motion && Input.IsMouseButtonPressed(MouseButton.Left))
        {
            UpdateActorMoveFromScreen(motion.Position, motion.CtrlPressed || motion.MetaPressed);
            GetViewport().SetInputAsHandled();
            return true;
        }

        if (inputEvent is InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false })
        {
            CancelActorMove();
            GetViewport().SetInputAsHandled();
            return true;
        }

        return false;
    }

    private void BeginActorMove(Vector2 screenPosition)
    {
        if (_document is null || _playMode || _selectedActor is null || _selectedActors.Count == 0)
            return;

        _actorMoveSelectedActors = _selectedActors
            .Where(_actors.Contains)
            .OrderBy(GetActorHierarchyDepth)
            .ToArray();
        if (_actorMoveSelectedActors.Length == 0 || !_actorMoveSelectedActors.Contains(_selectedActor))
        {
            ClearActorMoveState();
            return;
        }

        _actorMoveFocusName = _selectedActor.LegacyName;
        _actorMovePlanePoint = _selectedActor.GlobalPosition;
        _actorMovePlaneNormal = _camera.GlobalBasis.Z.Normalized();
        if (!TryGetActorMovePlanePoint(screenPosition, out Vector3 pointerPoint))
        {
            ClearActorMoveState();
            return;
        }

        _actorMovePointerOffset = _actorMovePlanePoint - pointerPoint;
        _actorMoveStartPointer = screenPosition;
        _actorMoveAffectedActors = _actors
            .Where(candidate => _actorMoveSelectedActors.Any(selected =>
                candidate == selected || selected.IsAncestorOf(candidate)))
            .OrderBy(GetActorHierarchyDepth)
            .ToArray();
        _actorMoveBefore = CaptureActorMoveStates(_actorMoveAffectedActors);
        _actorMovePending = true;
        _actorMoveDragging = false;
    }

    private void UpdateActorMoveFromScreen(Vector2 screenPosition, bool snapToGrid)
    {
        if (!_actorMovePending || _actorMoveBefore is null)
            return;
        if (!_actorMoveDragging)
        {
            if (screenPosition.DistanceSquaredTo(_actorMoveStartPointer) < 16f)
                return;
            _actorMoveDragging = true;
            _status.Text = "Drag to move; hold Ctrl/Cmd to snap to the legacy grid. Press Esc to cancel.";
        }

        if (!TryGetActorMovePlanePoint(screenPosition, out Vector3 pointerPoint)
            || !_actorMoveBefore.TryGetValue(_actorMoveFocusName, out ActorMoveSnapshot? focusState)
            || focusState is null)
            return;

        Vector3 focusPosition = pointerPoint + _actorMovePointerOffset;
        if (snapToGrid)
        {
            focusPosition.X = Mathf.Round(focusPosition.X / CopySnapHorizontal) * CopySnapHorizontal;
            focusPosition.Y = Mathf.Round(focusPosition.Y / CopySnapVertical) * CopySnapVertical;
            focusPosition.Z = Mathf.Round(focusPosition.Z / CopySnapHorizontal) * CopySnapHorizontal;
        }

        Vector3 delta = focusPosition - focusState.Transform.Origin;
        foreach (WorldActorNode actor in _actorMoveSelectedActors)
        {
            if (!GodotObject.IsInstanceValid(actor)
                || !_actorMoveBefore.TryGetValue(actor.LegacyName, out ActorMoveSnapshot? state)
                || state is null)
                continue;
            Transform3D movedTransform = state.Transform;
            movedTransform.Origin += delta;
            actor.GlobalTransform = movedTransform;
        }
        SaveActorMoveTransforms(_actorMoveAffectedActors);
    }

    private bool TryGetActorMovePlanePoint(Vector2 screenPosition, out Vector3 point)
    {
        Vector3 rayOrigin = _camera.ProjectRayOrigin(screenPosition);
        Vector3 rayDirection = _camera.ProjectRayNormal(screenPosition);
        float denominator = _actorMovePlaneNormal.Dot(rayDirection);
        if (Mathf.Abs(denominator) < 0.0001f)
        {
            point = Vector3.Zero;
            return false;
        }

        float distance = _actorMovePlaneNormal.Dot(_actorMovePlanePoint - rayOrigin) / denominator;
        if (distance < 0f)
        {
            point = Vector3.Zero;
            return false;
        }

        point = rayOrigin + rayDirection * distance;
        return true;
    }

    private void FinishActorMove()
    {
        if (!_actorMovePending)
            return;

        Dictionary<string, ActorMoveSnapshot>? before = _actorMoveBefore;
        bool moved = _actorMoveDragging && before is not null;
        WorldActorNode[] affectedActors = _actorMoveAffectedActors;
        int selectedCount = _actorMoveSelectedActors.Length;
        ClearActorMoveState();
        if (!moved || before is null)
            return;

        if (_autoSetSpawnOnMove)
        {
            foreach (WorldActorNode actor in affectedActors)
            {
                actor.State.WriteLegacyVector("spawnPosition", CoordinateSpace.ToLegacy(actor.GlobalPosition));
                actor.State.WriteLegacyQuaternion("spawnRotation", CoordinateSpace.ToLegacy(actor.GlobalBasis.GetRotationQuaternion()));
            }
        }

        SaveActorMoveTransforms(affectedActors);
        Dictionary<string, ActorMoveSnapshot> after = CaptureActorMoveStates(affectedActors);
        if (ActorMoveTransformsEqual(before, after))
        {
            ApplyActorMoveStates(before);
            return;
        }

        RecordHistory("Move",
            () => ApplyActorMoveStates(before),
            () => ApplyActorMoveStates(after));
        _status.Text = $"Moved {selectedCount} actor{(selectedCount == 1 ? "" : "s")}.";
    }

    private void CancelActorMove()
    {
        if (!_actorMovePending)
            return;

        Dictionary<string, ActorMoveSnapshot>? before = _actorMoveBefore;
        bool wasDragging = _actorMoveDragging;
        ClearActorMoveState();
        if (wasDragging && before is not null)
        {
            ApplyActorMoveStates(before);
            _status.Text = "Actor move canceled.";
        }
    }

    private void ClearActorMoveState()
    {
        _actorMovePending = false;
        _actorMoveDragging = false;
        _actorMoveFocusName = "";
        _actorMoveSelectedActors = Array.Empty<WorldActorNode>();
        _actorMoveAffectedActors = Array.Empty<WorldActorNode>();
        _actorMoveBefore = null;
    }

    private static Dictionary<string, ActorMoveSnapshot> CaptureActorMoveStates(IEnumerable<WorldActorNode> actors)
    {
        var states = new Dictionary<string, ActorMoveSnapshot>(StringComparer.Ordinal);
        foreach (WorldActorNode actor in actors)
        {
            states.Add(actor.LegacyName, new ActorMoveSnapshot(
                actor.GlobalTransform,
                actor.LinearVelocity,
                actor.AngularVelocity,
                actor.State.SpawnPosition,
                actor.State.SpawnRotation));
        }
        return states;
    }

    private void ApplyActorMoveStates(IReadOnlyDictionary<string, ActorMoveSnapshot> states)
    {
        foreach (WorldActorNode actor in _actors.OrderBy(GetActorHierarchyDepth))
        {
            if (!states.TryGetValue(actor.LegacyName, out ActorMoveSnapshot? state) || state is null)
                continue;
            actor.GlobalTransform = state.Transform;
            actor.LinearVelocity = state.Velocity;
            actor.AngularVelocity = state.AngularVelocity;
            actor.State.WriteLegacyVector("spawnPosition", CoordinateSpace.ToLegacy(state.SpawnPosition));
            actor.State.WriteLegacyQuaternion("spawnRotation", CoordinateSpace.ToLegacy(state.SpawnRotation));
            actor.SaveTransform();
        }
        if (_selectedActor is not null)
            SelectActor(_selectedActor);
    }

    private static bool ActorMoveTransformsEqual(
        IReadOnlyDictionary<string, ActorMoveSnapshot> left,
        IReadOnlyDictionary<string, ActorMoveSnapshot> right)
    {
        if (left.Count != right.Count)
            return false;
        foreach ((string actorName, ActorMoveSnapshot state) in left)
        {
            if (!right.TryGetValue(actorName, out ActorMoveSnapshot? other)
                || other is null
                || !state.Transform.IsEqualApprox(other.Transform))
                return false;
        }
        return true;
    }

    private static void SaveActorMoveTransforms(IEnumerable<WorldActorNode> actors)
    {
        foreach (WorldActorNode actor in actors)
            actor.SaveTransform();
    }

    private async void RunActorMoveSmoke()
    {
        int exitCode = 0;
        try
        {
            await WaitForUiFrames(3);
            if (_document is null || _welcome.Visible || !_editor.Visible)
                throw new InvalidOperationException("The actor-move smoke requires an open project in the editor.");

            WorldActorNode? actor = _actors.FirstOrDefault(candidate =>
            {
                if (candidate.IsOffstageEffective || _camera.ToLocal(candidate.GlobalPosition).Z >= 0f)
                    return false;
                Vector2 screenPosition = _camera.UnprojectPosition(candidate.GetWorldRenderBounds().GetCenter());
                return !IsPointerOverUi(screenPosition) && GetActorAtScreenPosition(screenPosition) == candidate;
            });
            if (actor is null)
                throw new InvalidOperationException("The sample has no visible actor available for the move smoke.");

            WorldActorNode? groupActor = _actors.FirstOrDefault(candidate =>
            {
                if (candidate == actor || candidate.IsOffstageEffective
                    || actor.IsAncestorOf(candidate) || candidate.IsAncestorOf(actor)
                    || _camera.ToLocal(candidate.GlobalPosition).Z >= 0f)
                    return false;
                Vector2 screenPosition = _camera.UnprojectPosition(candidate.GetWorldRenderBounds().GetCenter());
                return !IsPointerOverUi(screenPosition) && GetActorAtScreenPosition(screenPosition) == candidate;
            });
            if (groupActor is null)
                throw new InvalidOperationException("The sample has no second visible independent actor for group movement.");

            SetActorSelection(new[] { actor, groupActor }, actor);
            _autoSetSpawnOnMove = true;
            Vector2 pointerStart = _camera.UnprojectPosition(actor.GetWorldRenderBounds().GetCenter());
            Transform3D originalTransform = actor.GlobalTransform;
            Transform3D originalGroupTransform = groupActor.GlobalTransform;
            Vector3 originalSpawnPosition = actor.State.SpawnPosition;
            Quaternion originalSpawnRotation = actor.State.SpawnRotation;
            Vector3 originalGroupSpawnPosition = groupActor.State.SpawnPosition;
            Quaternion originalGroupSpawnRotation = groupActor.State.SpawnRotation;
            int historyBeforeMove = _historyPosition;
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = pointerStart
            });
            if (!_actorMovePending)
                throw new InvalidOperationException("Clicking a selected actor did not begin a move gesture.");

            Vector3 moveDelta = _camera.GlobalBasis.X.Normalized() * 2.5f;
            Vector2 pointerTarget = _camera.UnprojectPosition(originalTransform.Origin + moveDelta - _actorMovePointerOffset);
            UpdateActorMoveFromScreen(pointerTarget, snapToGrid: false);
            if (!_actorMoveDragging
                || actor.GlobalPosition.DistanceTo(originalTransform.Origin + moveDelta) > 0.001f
                || groupActor.GlobalPosition.DistanceTo(originalGroupTransform.Origin + moveDelta) > 0.001f)
                throw new InvalidOperationException(
                    $"Dragging did not translate the selected group on the camera-facing move plane: dragging={_actorMoveDragging}, "
                    + $"selected='{_selectedActor?.LegacyName}', actor={actor.GlobalPosition}, "
                    + $"group={groupActor.GlobalPosition}, offset={_actorMovePointerOffset}.");
            FinishActorMove();
            Transform3D movedTransform = actor.GlobalTransform;
            Transform3D movedGroupTransform = groupActor.GlobalTransform;
            Vector3 movedSpawnPosition = actor.State.SpawnPosition;
            Quaternion movedSpawnRotation = actor.State.SpawnRotation;
            Vector3 movedGroupSpawnPosition = groupActor.State.SpawnPosition;
            Quaternion movedGroupSpawnRotation = groupActor.State.SpawnRotation;
            if (_historyPosition != historyBeforeMove + 1
                || !movedSpawnPosition.IsEqualApprox(movedTransform.Origin)
                || !movedSpawnRotation.IsEqualApprox(movedTransform.Basis.GetRotationQuaternion())
                || !movedGroupSpawnPosition.IsEqualApprox(movedGroupTransform.Origin)
                || !movedGroupSpawnRotation.IsEqualApprox(movedGroupTransform.Basis.GetRotationQuaternion()))
                throw new InvalidOperationException("Group movement was not one undoable edit with both restart transforms updated.");

            UndoEdit();
            if (!actor.GlobalTransform.IsEqualApprox(originalTransform)
                || !groupActor.GlobalTransform.IsEqualApprox(originalGroupTransform)
                || !actor.State.SpawnPosition.IsEqualApprox(originalSpawnPosition)
                || !actor.State.SpawnRotation.IsEqualApprox(originalSpawnRotation)
                || !groupActor.State.SpawnPosition.IsEqualApprox(originalGroupSpawnPosition)
                || !groupActor.State.SpawnRotation.IsEqualApprox(originalGroupSpawnRotation))
                throw new InvalidOperationException("Undo did not restore both actors and their original restart states.");
            RedoEdit();
            if (!actor.GlobalTransform.IsEqualApprox(movedTransform)
                || !groupActor.GlobalTransform.IsEqualApprox(movedGroupTransform)
                || !actor.State.SpawnPosition.IsEqualApprox(movedSpawnPosition)
                || !actor.State.SpawnRotation.IsEqualApprox(movedSpawnRotation)
                || !groupActor.State.SpawnPosition.IsEqualApprox(movedGroupSpawnPosition)
                || !groupActor.State.SpawnRotation.IsEqualApprox(movedGroupSpawnRotation))
                throw new InvalidOperationException("Redo did not restore both moved actors and their restart states.");

            pointerStart = _camera.UnprojectPosition(actor.GetWorldRenderBounds().GetCenter());
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = pointerStart
            });
            Vector3 snappedGroupStart = groupActor.GlobalPosition;
            Vector3 snappedStart = actor.GlobalPosition;
            Vector3 snappedTarget = snappedStart + _camera.GlobalBasis.X.Normalized() * 1.7f + _camera.GlobalBasis.Y.Normalized() * 1.3f;
            pointerTarget = _camera.UnprojectPosition(snappedTarget - _actorMovePointerOffset);
            UpdateActorMoveFromScreen(pointerTarget, snapToGrid: true);
            Vector3 snappedPosition = actor.GlobalPosition;
            if (!Mathf.IsEqualApprox(snappedPosition.X / CopySnapHorizontal, Mathf.Round(snappedPosition.X / CopySnapHorizontal))
                || !Mathf.IsEqualApprox(snappedPosition.Y / CopySnapVertical, Mathf.Round(snappedPosition.Y / CopySnapVertical))
                || !Mathf.IsEqualApprox(snappedPosition.Z / CopySnapHorizontal, Mathf.Round(snappedPosition.Z / CopySnapHorizontal))
                || groupActor.GlobalPosition.DistanceTo(snappedGroupStart + snappedPosition - snappedStart) > 0.001f)
                throw new InvalidOperationException("Ctrl/Cmd snap did not align the move focus to the legacy grid and translate its group together.");
            FinishActorMove();
            _autoSetSpawnOnMove = false;
            pointerStart = _camera.UnprojectPosition(actor.GetWorldRenderBounds().GetCenter());
            Transform3D beforeNoAutoset = actor.GlobalTransform;
            Transform3D groupBeforeNoAutoset = groupActor.GlobalTransform;
            Vector3 spawnBeforeNoAutoset = actor.State.SpawnPosition;
            Quaternion spawnRotationBeforeNoAutoset = actor.State.SpawnRotation;
            Vector3 groupSpawnBeforeNoAutoset = groupActor.State.SpawnPosition;
            Quaternion groupSpawnRotationBeforeNoAutoset = groupActor.State.SpawnRotation;
            int historyBeforeNoAutoset = _historyPosition;
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = pointerStart
            });
            if (!_actorMovePending)
                throw new InvalidOperationException("The move gesture did not start with automatic restart updates disabled.");
            Vector3 noAutosetDelta = _camera.GlobalBasis.X.Normalized() * 1.3f;
            pointerTarget = _camera.UnprojectPosition(beforeNoAutoset.Origin + noAutosetDelta - _actorMovePointerOffset);
            UpdateActorMoveFromScreen(pointerTarget, snapToGrid: false);
            if (!_actorMoveDragging)
                throw new InvalidOperationException("The actor did not begin moving with automatic restart updates disabled.");
            FinishActorMove();
            Transform3D movedWithoutAutoset = actor.GlobalTransform;
            Transform3D movedGroupWithoutAutoset = groupActor.GlobalTransform;
            if (actor.GlobalPosition.DistanceTo(beforeNoAutoset.Origin + noAutosetDelta) > 0.001f
                || !movedWithoutAutoset.Basis.IsEqualApprox(beforeNoAutoset.Basis)
                || groupActor.GlobalPosition.DistanceTo(groupBeforeNoAutoset.Origin + noAutosetDelta) > 0.001f
                || !movedGroupWithoutAutoset.Basis.IsEqualApprox(groupBeforeNoAutoset.Basis)
                || !actor.State.SpawnPosition.IsEqualApprox(spawnBeforeNoAutoset)
                || !actor.State.SpawnRotation.IsEqualApprox(spawnRotationBeforeNoAutoset)
                || !groupActor.State.SpawnPosition.IsEqualApprox(groupSpawnBeforeNoAutoset)
                || !groupActor.State.SpawnRotation.IsEqualApprox(groupSpawnRotationBeforeNoAutoset)
                || _historyPosition != historyBeforeNoAutoset + 1)
                throw new InvalidOperationException(
                    $"Move with automatic restart updates disabled changed spawn state or missed history: "
                    + $"actor={actor.GlobalPosition}, expected={beforeNoAutoset.Origin + noAutosetDelta}, "
                    + $"group={groupActor.GlobalPosition}, expectedGroup={groupBeforeNoAutoset.Origin + noAutosetDelta}, "
                    + $"spawn={actor.State.SpawnPosition}/{spawnBeforeNoAutoset}, history={_historyPosition}/{historyBeforeNoAutoset + 1}.");
            UndoEdit();
            if (!actor.GlobalTransform.IsEqualApprox(beforeNoAutoset)
                || !groupActor.GlobalTransform.IsEqualApprox(groupBeforeNoAutoset)
                || !actor.State.SpawnPosition.IsEqualApprox(spawnBeforeNoAutoset)
                || !groupActor.State.SpawnPosition.IsEqualApprox(groupSpawnBeforeNoAutoset))
                throw new InvalidOperationException("Undo did not restore the move with automatic restart updates disabled.");
            RedoEdit();
            if (!actor.GlobalTransform.IsEqualApprox(movedWithoutAutoset)
                || !groupActor.GlobalTransform.IsEqualApprox(movedGroupWithoutAutoset)
                || !actor.State.SpawnPosition.IsEqualApprox(spawnBeforeNoAutoset)
                || !groupActor.State.SpawnPosition.IsEqualApprox(groupSpawnBeforeNoAutoset))
                throw new InvalidOperationException("Redo did not preserve restart state with automatic updates disabled.");
            _autoSetSpawnOnMove = true;

            pointerStart = _camera.UnprojectPosition(actor.GetWorldRenderBounds().GetCenter());
            Transform3D beforeCancel = actor.GlobalTransform;
            Vector3 spawnBeforeCancel = actor.State.SpawnPosition;
            int historyBeforeCancel = _historyPosition;
            Transform3D groupBeforeCancel = groupActor.GlobalTransform;
            Vector3 groupSpawnBeforeCancel = groupActor.State.SpawnPosition;
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = pointerStart
            });
            pointerTarget = _camera.UnprojectPosition(actor.GlobalPosition + _camera.GlobalBasis.X.Normalized() * 2f - _actorMovePointerOffset);
            UpdateActorMoveFromScreen(pointerTarget, snapToGrid: false);
            CancelActorMove();
            if (!actor.GlobalTransform.IsEqualApprox(beforeCancel)
                || !groupActor.GlobalTransform.IsEqualApprox(groupBeforeCancel)
                || !actor.State.SpawnPosition.IsEqualApprox(spawnBeforeCancel)
                || !groupActor.State.SpawnPosition.IsEqualApprox(groupSpawnBeforeCancel)
                || _historyPosition != historyBeforeCancel)
                throw new InvalidOperationException("Escape cancellation did not restore both actors without creating history.");

            string screenshotPath = ProjectSettings.GlobalizePath("res://.godot/actor-move-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(screenshotPath) != Error.Ok)
                throw new InvalidOperationException("Could not write the actor-move smoke screenshot.");
            GD.Print($"ACTOR_MOVE_SMOKE_OK drag=true cameraPlane=true groupSelection=true snap=legacy-grid=true autoSpawn=enabled+disabled undo=true redo=true cancel=true screenshot={screenshotPath}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ACTOR_MOVE_SMOKE_FAILED: {exception}");
        }
        finally
        {
            GetTree().Quit(exitCode);
        }
    }
}
