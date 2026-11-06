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
    private LegacyActorMoveGizmoAxis _actorMoveConstraintAxis;
    private Vector3 _actorMoveAxisDirection;
    private Vector2 _actorMoveAxisScreenDirection;
    private float _actorMoveAxisWorldLength;
    private bool _actorMoveAxisUsesLocalSpace;
    private float _actorMoveGizmoWorldLength;

    private void UpdateActorMoveGizmo()
    {
        if (_actorMoveGizmo is null || !IsInstanceValid(_actorMoveGizmo))
            return;

        bool canShow = _document is not null
            && !_welcome.Visible
            && !_playMode
            && _selectedActor is not null
            && _actors.Contains(_selectedActor)
            && !_selectedActor.IsOffstageEffective
            && !_textToolActive
            && !_copyPlacementActive
            && !_terrainCopyActive
            && !_settingTransformParent
            && !_settingSpawnParent;
        if (!canShow || _camera.ToLocal(_selectedActor!.GlobalPosition).Z >= 0f)
        {
            _actorMoveGizmoWorldLength = 0f;
            _actorMoveGizmo.SetHandles(false, Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero);
            return;
        }

        Vector3 pivot = _selectedActor.GlobalPosition;
        Vector2 originScreen = _camera.UnprojectPosition(pivot);
        if (!GetViewport().GetVisibleRect().HasPoint(originScreen))
        {
            _actorMoveGizmoWorldLength = 0f;
            _actorMoveGizmo.SetHandles(false, Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero);
            return;
        }

        Basis axes = _actorMoveLocalSpace && _selectedActors.Count == 1
            ? _selectedActor.GlobalBasis.Orthonormalized()
            : Basis.Identity;
        float depth = _camera.GlobalPosition.DistanceTo(pivot);
        float viewportHeight = Mathf.Max(GetViewport().GetVisibleRect().Size.Y, 1f);
        float worldLength = Mathf.Clamp(
            84f * 2f * depth * Mathf.Tan(Mathf.DegToRad(_camera.Fov) * 0.5f) / viewportHeight,
            0.75f,
            4f);
        _actorMoveGizmoWorldLength = worldLength;
        if (_actorRotateToolActive)
        {
            _actorMoveGizmo.SetHandles(false, Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero);
            return;
        }
        _actorMoveGizmo.SetHandles(
            true,
            originScreen,
            _camera.UnprojectPosition(pivot + axes.X.Normalized() * worldLength),
            _camera.UnprojectPosition(pivot + axes.Y.Normalized() * worldLength),
            _camera.UnprojectPosition(pivot + axes.Z.Normalized() * worldLength));
        _actorMoveGizmo.SetHoveredAxis(
            _actorMoveGizmo.TryPickAxis(GetViewport().GetMousePosition(), out LegacyActorMoveGizmoAxis hoveredAxis)
                ? hoveredAxis
                : LegacyActorMoveGizmoAxis.None);
    }

    private Vector3 GetActorMoveAxisDirection(LegacyActorMoveGizmoAxis axis)
    {
        Vector3 localDirection = axis switch
        {
            LegacyActorMoveGizmoAxis.X => Vector3.Right,
            LegacyActorMoveGizmoAxis.Y => Vector3.Up,
            LegacyActorMoveGizmoAxis.Z => Basis.Identity.Z,
            _ => Vector3.Zero
        };
        if (_actorMoveLocalSpace && _selectedActors.Count == 1 && _selectedActor is not null)
            localDirection = _selectedActor.GlobalBasis.Orthonormalized() * localDirection;
        return localDirection.Normalized();
    }


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

    private void BeginActorMove(
        Vector2 screenPosition,
        LegacyActorMoveGizmoAxis axis = LegacyActorMoveGizmoAxis.None)
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
        _actorMoveStartPointer = screenPosition;
        _actorMoveConstraintAxis = axis;
        _actorMoveAxisUsesLocalSpace = axis != LegacyActorMoveGizmoAxis.None
            && _actorMoveLocalSpace
            && _actorMoveSelectedActors.Length == 1;
        if (axis == LegacyActorMoveGizmoAxis.None)
        {
            if (!TryGetActorMovePlanePoint(screenPosition, out Vector3 pointerPoint))
            {
                ClearActorMoveState();
                return;
            }
            _actorMovePointerOffset = _actorMovePlanePoint - pointerPoint;
        }
        else
        {
            _actorMoveAxisDirection = GetActorMoveAxisDirection(axis);
            _actorMoveAxisScreenDirection = _actorMoveGizmo.GetAxisScreenDirection(axis);
            _actorMoveAxisWorldLength = _actorMoveGizmoWorldLength;
            if (_actorMoveAxisScreenDirection.LengthSquared() < 225f || _actorMoveAxisWorldLength <= 0f)
            {
                ClearActorMoveState();
                return;
            }
            _actorMovePointerOffset = Vector3.Zero;
        }
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
            string axisName = _actorMoveConstraintAxis switch
            {
                LegacyActorMoveGizmoAxis.X => "X",
                LegacyActorMoveGizmoAxis.Y => "Y",
                LegacyActorMoveGizmoAxis.Z => "Z",
                _ => ""
            };
            _status.Text = _actorMoveConstraintAxis == LegacyActorMoveGizmoAxis.None
                ? _actorMoveSnapToGrid
                    ? "Drag to move; snap-to-grid is on. Press Esc to cancel."
                    : "Drag to move; hold Ctrl/Cmd to snap to the legacy grid. Press Esc to cancel."
                : $"Drag along {(_actorMoveAxisUsesLocalSpace ? "local" : "world")} {axisName}; hold Ctrl/Cmd to snap. Press Esc to cancel.";
        }

        if (!_actorMoveBefore.TryGetValue(_actorMoveFocusName, out ActorMoveSnapshot? focusState)
            || focusState is null)
            return;

        bool shouldSnap = snapToGrid || _actorMoveSnapToGrid;
        Vector3 focusPosition;
        if (_actorMoveConstraintAxis == LegacyActorMoveGizmoAxis.None)
        {
            if (!TryGetActorMovePlanePoint(screenPosition, out Vector3 pointerPoint))
                return;
            focusPosition = pointerPoint + _actorMovePointerOffset;
            if (shouldSnap)
            {
                focusPosition.X = Mathf.Round(focusPosition.X / CopySnapHorizontal) * CopySnapHorizontal;
                focusPosition.Y = Mathf.Round(focusPosition.Y / CopySnapVertical) * CopySnapVertical;
                focusPosition.Z = Mathf.Round(focusPosition.Z / CopySnapHorizontal) * CopySnapHorizontal;
            }
        }
        else
        {
            float screenLengthSquared = _actorMoveAxisScreenDirection.LengthSquared();
            if (screenLengthSquared < 225f)
                return;
            float moveAmount = (screenPosition - _actorMoveStartPointer)
                .Dot(_actorMoveAxisScreenDirection) / screenLengthSquared * _actorMoveAxisWorldLength;
            if (shouldSnap)
            {
                float snapStep = _actorMoveConstraintAxis == LegacyActorMoveGizmoAxis.Y
                    ? CopySnapVertical
                    : CopySnapHorizontal;
                if (_actorMoveAxisUsesLocalSpace)
                {
                    moveAmount = Mathf.Round(moveAmount / snapStep) * snapStep;
                }
                else
                {
                    int coordinate = _actorMoveConstraintAxis switch
                    {
                        LegacyActorMoveGizmoAxis.X => 0,
                        LegacyActorMoveGizmoAxis.Y => 1,
                        _ => 2
                    };
                    float originCoordinate = focusState.Transform.Origin[coordinate];
                    moveAmount = Mathf.Round((originCoordinate + moveAmount) / snapStep) * snapStep - originCoordinate;
                }
            }
            focusPosition = focusState.Transform.Origin + _actorMoveAxisDirection * moveAmount;
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
        _actorMoveConstraintAxis = LegacyActorMoveGizmoAxis.None;
        _actorMoveAxisDirection = Vector3.Zero;
        _actorMoveAxisScreenDirection = Vector2.Zero;
        _actorMoveAxisWorldLength = 0f;
        _actorMoveAxisUsesLocalSpace = false;
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
    private Vector2 FindActorMoveSmokeDragStart(WorldActorNode actor)
    {
        Vector2 center = _camera.UnprojectPosition(actor.GetWorldRenderBounds().GetCenter());
        for (int ring = 0; ring < 4; ring++)
        {
            float spacing = 8f + ring * 8f;
            for (int candidate = -1; candidate < 8; candidate++)
            {
                Vector2 offset = candidate switch
                {
                    -1 => Vector2.Zero,
                    0 => new Vector2(spacing, 0f),
                    1 => new Vector2(-spacing, 0f),
                    2 => new Vector2(0f, spacing),
                    3 => new Vector2(0f, -spacing),
                    4 => new Vector2(spacing, spacing),
                    5 => new Vector2(-spacing, spacing),
                    6 => new Vector2(spacing, -spacing),
                    _ => new Vector2(-spacing, -spacing)
                };
                Vector2 position = center + offset;
                if (!IsPointerOverUi(position)
                    && GetActorAtScreenPosition(position) == actor
                    && !_actorMoveGizmo.TryPickAxis(position, out _))
                    return position;
            }
        }
        throw new InvalidOperationException($"Could not find an unobstructed free-drag point on actor '{actor.LegacyName}'.");
    }


    private async void RunActorMoveSmoke()
    {
        int exitCode = 0;
        bool originalMoveSnap = _actorMoveSnapToGrid;
        bool originalLocalSpace = _actorMoveLocalSpace;
        bool restoreLocalSpacePreference = false;
        bool restoreMoveSnapPreference = false;
        try
        {
            await WaitForUiFrames(3);
            if (_document is null || _welcome.Visible || !_editor.Visible)
                throw new InvalidOperationException("The actor-move smoke requires an open project in the editor.");
            if (!_actorMoveSnapToggle.IsVisibleInTree()
                || !_actorMoveSnapToggle.GetGlobalRect().Intersects(GetViewport().GetVisibleRect()))
                throw new InvalidOperationException("The persistent move-snap toggle is not visible in the editor.");
            if (!_actorMoveLocalSpaceToggle.IsVisibleInTree()
                || !_actorMoveLocalSpaceToggle.GetGlobalRect().Intersects(GetViewport().GetVisibleRect()))
                throw new InvalidOperationException("The local-axes toggle is not visible in the editor.");
            restoreMoveSnapPreference = true;
            SetActorMoveSnapPreference(false);
            restoreLocalSpacePreference = true;
            SetActorMoveLocalSpacePreference(false);

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
            Vector2 pointerStart = FindActorMoveSmokeDragStart(actor);
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

            pointerStart = _camera.UnprojectPosition(actor.GlobalPosition);
            BeginActorMove(pointerStart);
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
            restoreMoveSnapPreference = true;
            _actorMoveSnapToggle.SetPressedNoSignal(true);
            _actorMoveSnapToggle.EmitSignal("toggled", true);
            var savedMovePreferences = new ConfigFile();
            if (!_actorMoveSnapToGrid
                || !_actorMoveSnapToggle.ButtonPressed
                || savedMovePreferences.Load(EditorSettingsPath) != Error.Ok
                || !savedMovePreferences.GetValue("editor", "move_snap_to_grid", false).AsBool())
                throw new InvalidOperationException("The persistent move-snap toggle did not save and restore its enabled preference.");

            Vector3 persistentSnapStart = actor.GlobalPosition;
            Vector3 persistentSnapGroupStart = groupActor.GlobalPosition;
            int historyBeforePersistentSnap = _historyPosition;
            pointerStart = _camera.UnprojectPosition(actor.GlobalPosition);
            BeginActorMove(pointerStart);
            Vector3 persistentSnapTarget = persistentSnapStart
                + _camera.GlobalBasis.X.Normalized() * 1.7f
                + _camera.GlobalBasis.Y.Normalized() * 1.3f;
            pointerTarget = _camera.UnprojectPosition(persistentSnapTarget - _actorMovePointerOffset);
            UpdateActorMoveFromScreen(pointerTarget, snapToGrid: false);
            Vector3 persistentSnapPosition = actor.GlobalPosition;
            if (!_actorMoveDragging
                || persistentSnapPosition.DistanceTo(persistentSnapStart) < 0.01f
                || !Mathf.IsEqualApprox(persistentSnapPosition.X / CopySnapHorizontal, Mathf.Round(persistentSnapPosition.X / CopySnapHorizontal))
                || !Mathf.IsEqualApprox(persistentSnapPosition.Y / CopySnapVertical, Mathf.Round(persistentSnapPosition.Y / CopySnapVertical))
                || !Mathf.IsEqualApprox(persistentSnapPosition.Z / CopySnapHorizontal, Mathf.Round(persistentSnapPosition.Z / CopySnapHorizontal))
                || groupActor.GlobalPosition.DistanceTo(persistentSnapGroupStart + persistentSnapPosition - persistentSnapStart) > 0.001f)
                throw new InvalidOperationException("The persistent move-snap toggle did not snap the moved group when Ctrl/Cmd was not pressed.");
            FinishActorMove();
            if (_historyPosition != historyBeforePersistentSnap + 1)
                throw new InvalidOperationException("Persistent snap movement was not committed as one undoable edit.");
            SetActorMoveSnapPreference(false);
            _autoSetSpawnOnMove = false;
            pointerStart = _camera.UnprojectPosition(actor.GlobalPosition);
            Transform3D beforeNoAutoset = actor.GlobalTransform;
            Transform3D groupBeforeNoAutoset = groupActor.GlobalTransform;
            Vector3 spawnBeforeNoAutoset = actor.State.SpawnPosition;
            Quaternion spawnRotationBeforeNoAutoset = actor.State.SpawnRotation;
            Vector3 groupSpawnBeforeNoAutoset = groupActor.State.SpawnPosition;
            Quaternion groupSpawnRotationBeforeNoAutoset = groupActor.State.SpawnRotation;
            int historyBeforeNoAutoset = _historyPosition;
            BeginActorMove(pointerStart);
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

            pointerStart = _camera.UnprojectPosition(actor.GlobalPosition);
            Transform3D beforeCancel = actor.GlobalTransform;
            Vector3 spawnBeforeCancel = actor.State.SpawnPosition;
            int historyBeforeCancel = _historyPosition;
            Transform3D groupBeforeCancel = groupActor.GlobalTransform;
            Vector3 groupSpawnBeforeCancel = groupActor.State.SpawnPosition;
            BeginActorMove(pointerStart);
            pointerTarget = _camera.UnprojectPosition(actor.GlobalPosition + _camera.GlobalBasis.X.Normalized() * 2f - _actorMovePointerOffset);
            UpdateActorMoveFromScreen(pointerTarget, snapToGrid: false);
            CancelActorMove();
            if (!actor.GlobalTransform.IsEqualApprox(beforeCancel)
                || !groupActor.GlobalTransform.IsEqualApprox(groupBeforeCancel)
                || !actor.State.SpawnPosition.IsEqualApprox(spawnBeforeCancel)
                || !groupActor.State.SpawnPosition.IsEqualApprox(groupSpawnBeforeCancel)
                || _historyPosition != historyBeforeCancel)
                throw new InvalidOperationException("Escape cancellation did not restore both actors without creating history.");
            SetActorSelection(new[] { actor, groupActor }, actor);
            UpdateActorMoveGizmo();
            Vector3 worldXAxis = GetActorMoveAxisDirection(LegacyActorMoveGizmoAxis.X);
            Vector2 worldAxisScreen = _actorMoveGizmo.GetAxisScreenDirection(LegacyActorMoveGizmoAxis.X);
            Vector2 gizmoOrigin = _camera.UnprojectPosition(actor.GlobalPosition);
            Vector2 worldHandleStart = gizmoOrigin + worldAxisScreen * 0.55f;
            if (!_actorMoveGizmo.Visible
                || worldXAxis.DistanceTo(Vector3.Right) > 0.001f
                || !_actorMoveGizmo.TryPickAxis(worldHandleStart, out LegacyActorMoveGizmoAxis pickedWorldAxis)
                || pickedWorldAxis != LegacyActorMoveGizmoAxis.X)
                throw new InvalidOperationException("The visible move gizmo did not expose a pickable world X axis.");

            Transform3D worldAxisActorBefore = actor.GlobalTransform;
            Transform3D worldAxisGroupBefore = groupActor.GlobalTransform;
            int historyBeforeWorldAxis = _historyPosition;
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = worldHandleStart
            });
            if (!_actorMovePending || _actorMoveConstraintAxis != LegacyActorMoveGizmoAxis.X)
                throw new InvalidOperationException("Clicking the X handle did not begin an axis-constrained move.");
            const float worldAxisDistance = 2.5f;
            Vector2 worldAxisTarget = worldHandleStart
                + worldAxisScreen * (worldAxisDistance / _actorMoveAxisWorldLength);
            UpdateActorMoveFromScreen(worldAxisTarget, snapToGrid: true);
            Vector3 worldActorDelta = actor.GlobalPosition - worldAxisActorBefore.Origin;
            Vector3 worldAxisRemainder = worldActorDelta - worldXAxis * worldActorDelta.Dot(worldXAxis);
            Vector3 worldGroupDelta = groupActor.GlobalPosition - worldAxisGroupBefore.Origin;
            bool worldAxisPositionSnapped = Mathf.IsEqualApprox(
                actor.GlobalPosition.X / CopySnapHorizontal,
                Mathf.Round(actor.GlobalPosition.X / CopySnapHorizontal));
            if (!_actorMoveDragging
                || Mathf.Abs(worldActorDelta.Dot(worldXAxis)) < 0.01f
                || !worldAxisPositionSnapped
                || worldAxisRemainder.Length() > 0.001f
                || worldGroupDelta.DistanceTo(worldActorDelta) > 0.001f)
                throw new InvalidOperationException(
                    $"World X drag failed: dragging={_actorMoveDragging}, actorDelta={worldActorDelta}, "
                    + $"worldXGridAligned={worldAxisPositionSnapped}, offAxis={worldAxisRemainder}, "
                    + $"groupDelta={worldGroupDelta}.");
            FinishActorMove();
            Transform3D worldAxisActorAfter = actor.GlobalTransform;
            Transform3D worldAxisGroupAfter = groupActor.GlobalTransform;
            if (_historyPosition != historyBeforeWorldAxis + 1)
                throw new InvalidOperationException("World-axis movement did not create one undoable edit.");
            UndoEdit();
            if (!actor.GlobalTransform.IsEqualApprox(worldAxisActorBefore)
                || !groupActor.GlobalTransform.IsEqualApprox(worldAxisGroupBefore))
                throw new InvalidOperationException("Undo did not restore the world-axis group move.");
            RedoEdit();
            if (!actor.GlobalTransform.IsEqualApprox(worldAxisActorAfter)
                || !groupActor.GlobalTransform.IsEqualApprox(worldAxisGroupAfter))
                throw new InvalidOperationException("Redo did not restore the world-axis group move.");
            UndoEdit();

            SetActorSelection(new[] { actor }, actor);
            Transform3D actorTransformBeforeLocalTest = actor.GlobalTransform;
            try
            {
                actor.GlobalRotationDegrees = new Vector3(0f, 45f, 0f);
                actor.SaveTransform();
                _actorMoveLocalSpaceToggle.SetPressedNoSignal(true);
                _actorMoveLocalSpaceToggle.EmitSignal("toggled", true);
                var savedLocalPreferences = new ConfigFile();
                if (!_actorMoveLocalSpace
                    || savedLocalPreferences.Load(EditorSettingsPath) != Error.Ok
                    || !savedLocalPreferences.GetValue("editor", "move_local_space", false).AsBool())
                    throw new InvalidOperationException("The local-axis toggle did not persist its enabled preference.");

                UpdateActorMoveGizmo();
                Vector3 localXAxis = GetActorMoveAxisDirection(LegacyActorMoveGizmoAxis.X);
                Vector2 localAxisScreen = _actorMoveGizmo.GetAxisScreenDirection(LegacyActorMoveGizmoAxis.X);
                Vector2 localHandleStart = _camera.UnprojectPosition(actor.GlobalPosition) + localAxisScreen * 0.55f;
                if (!_actorMoveGizmo.Visible
                    || localXAxis.DistanceTo(actor.GlobalBasis.Orthonormalized().X.Normalized()) > 0.001f
                    || Mathf.Abs(localXAxis.Dot(Vector3.Right)) > 0.99f
                    || !_actorMoveGizmo.TryPickAxis(localHandleStart, out LegacyActorMoveGizmoAxis pickedLocalAxis)
                    || pickedLocalAxis != LegacyActorMoveGizmoAxis.X)
                    throw new InvalidOperationException("The local movement gizmo did not follow the selected actor's rotated X axis.");

                Transform3D localAxisActorBefore = actor.GlobalTransform;
                int historyBeforeLocalAxis = _historyPosition;
                _UnhandledInput(new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left,
                    Pressed = true,
                    Position = localHandleStart
                });
                if (!_actorMovePending
                    || _actorMoveConstraintAxis != LegacyActorMoveGizmoAxis.X
                    || !_actorMoveAxisUsesLocalSpace)
                    throw new InvalidOperationException("Clicking the local X handle did not begin a local-axis move.");
                const float localAxisDistance = 2.5f;
                Vector2 localAxisTarget = localHandleStart
                    + localAxisScreen * (localAxisDistance / _actorMoveAxisWorldLength);
                UpdateActorMoveFromScreen(localAxisTarget, snapToGrid: true);
                Vector3 localActorDelta = actor.GlobalPosition - localAxisActorBefore.Origin;
                Vector3 localAxisRemainder = localActorDelta - localXAxis * localActorDelta.Dot(localXAxis);
                if (!_actorMoveDragging
                    || Mathf.Abs(localActorDelta.Dot(localXAxis) - localAxisDistance) > 0.01f
                    || localAxisRemainder.Length() > 0.001f)
                    throw new InvalidOperationException("Local X dragging moved outside the selected actor's local axis.");
                FinishActorMove();
                Transform3D localAxisActorAfter = actor.GlobalTransform;
                if (_historyPosition != historyBeforeLocalAxis + 1)
                    throw new InvalidOperationException("Local-axis movement did not create one undoable edit.");
                UndoEdit();
                if (!actor.GlobalTransform.IsEqualApprox(localAxisActorBefore))
                    throw new InvalidOperationException("Undo did not restore the local-axis move.");
                RedoEdit();
                if (!actor.GlobalTransform.IsEqualApprox(localAxisActorAfter))
                    throw new InvalidOperationException("Redo did not restore the local-axis move.");
                UndoEdit();
            }
            finally
            {
                actor.GlobalTransform = actorTransformBeforeLocalTest;
                actor.SaveTransform();
                SetActorMoveLocalSpacePreference(false);
            }


            string screenshotPath = ProjectSettings.GlobalizePath("res://.godot/actor-move-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(screenshotPath) != Error.Ok)
                throw new InvalidOperationException("Could not write the actor-move smoke screenshot.");
            GD.Print($"ACTOR_MOVE_SMOKE_OK drag=true cameraPlane=true groupSelection=true snap=legacy-grid+persistent-toggle+persistence axes=world+local constrainedUndoRedo=true localSpace=persistent=true autoSpawn=enabled+disabled undo=true redo=true cancel=true screenshot={screenshotPath}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ACTOR_MOVE_SMOKE_FAILED: {exception}");
        }
        finally
        {
            if (restoreMoveSnapPreference)
                SetActorMoveSnapPreference(originalMoveSnap);
            if (restoreLocalSpacePreference)
                SetActorMoveLocalSpacePreference(originalLocalSpace);
            GetTree().Quit(exitCode);
        }
    }
}
