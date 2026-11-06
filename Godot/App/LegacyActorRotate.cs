using System;
using System.Collections.Generic;
using System.Linq;
using GBNG.World;
using GBNG.Domain;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private LegacyActorRotateGizmoControl _actorRotateGizmo = null!;
    private CheckBox _actorRotateToolToggle = null!;
    private CheckBox _actorRotateSnapToggle = null!;
    private bool _actorRotateToolActive;
    private bool _actorRotateSnap;
    private bool _actorRotatePending;
    private bool _actorRotateDragging;
    private bool _actorRotateUsesLocalSpace;
    private Vector2 _actorRotateStartPointer;
    private Vector3 _actorRotatePivot;
    private Vector3 _actorRotateAxisDirection;
    private Vector3 _actorRotateLastRadialDirection;
    private float _actorRotateAccumulatedRadians;
    private LegacyActorMoveGizmoAxis _actorRotateConstraintAxis;
    private WorldActorNode[] _actorRotateSelectedActors = Array.Empty<WorldActorNode>();
    private WorldActorNode[] _actorRotateAffectedActors = Array.Empty<WorldActorNode>();
    private Dictionary<string, ActorMoveSnapshot>? _actorRotateBefore;
    private bool _actorRotateProjectionCached;
    private Vector3 _actorRotateProjectionPivot;
    private Basis _actorRotateProjectionAxes;
    private Transform3D _actorRotateProjectionCameraTransform;
    private Vector2 _actorRotateProjectionViewportSize;
    private Vector2 _actorRotateProjectionOrigin;
    private float _actorRotateProjectionFov;
    private float _actorRotateProjectionWorldLength;

    private void UpdateActorRotateGizmo()
    {
        if (_actorRotateGizmo is null || !IsInstanceValid(_actorRotateGizmo))
            return;

        bool canShow = _actorRotateToolActive
            && _document is not null
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
        if (!canShow)
        {
            _actorRotateGizmo.SetHandlesVisible(false);
            return;
        }

        int selectedCount = 0;
        Vector3 pivot = Vector3.Zero;
        foreach (WorldActorNode actor in _selectedActors)
        {
            if (!_actors.Contains(actor) || !GodotObject.IsInstanceValid(actor))
                continue;
            pivot += actor.GlobalPosition;
            selectedCount++;
        }
        if (selectedCount == 0)
        {
            _actorRotateGizmo.SetHandlesVisible(false);
            return;
        }
        pivot /= selectedCount;
        if (_camera.ToLocal(pivot).Z >= 0f)
        {
            _actorRotateGizmo.SetHandlesVisible(false);
            return;
        }

        Vector2 originScreen = _camera.UnprojectPosition(pivot);
        if (!GetViewport().GetVisibleRect().HasPoint(originScreen))
        {
            _actorRotateGizmo.SetHandlesVisible(false);
            return;
        }

        Basis axes = _actorMoveLocalSpace && selectedCount == 1
            ? _selectedActor!.GlobalBasis.Orthonormalized()
            : Basis.Identity;
        float depth = _camera.GlobalPosition.DistanceTo(pivot);
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        float viewportHeight = Mathf.Max(viewportSize.Y, 1f);
        float worldLength = Mathf.Clamp(
            84f * 2f * depth * Mathf.Tan(Mathf.DegToRad(_camera.Fov) * 0.5f) / viewportHeight,
            0.75f,
            4f);

        _actorRotatePivot = pivot;
        _actorRotateGizmo.SetHandlesVisible(true);
        Transform3D cameraTransform = _camera.GlobalTransform;
        bool projectionChanged = !_actorRotateProjectionCached
            || _actorRotateProjectionPivot != pivot
            || _actorRotateProjectionAxes != axes
            || _actorRotateProjectionCameraTransform != cameraTransform
            || _actorRotateProjectionViewportSize != viewportSize
            || _actorRotateProjectionOrigin != originScreen
            || _actorRotateProjectionFov != _camera.Fov
            || _actorRotateProjectionWorldLength != worldLength;
        if (projectionChanged)
        {
            ProjectActorRotateRing(LegacyActorMoveGizmoAxis.X, pivot, axes.Y, axes.Z, worldLength * 0.9f);
            ProjectActorRotateRing(LegacyActorMoveGizmoAxis.Y, pivot, axes.Z, axes.X, worldLength * 0.78f);
            ProjectActorRotateRing(LegacyActorMoveGizmoAxis.Z, pivot, axes.X, axes.Y, worldLength * 0.66f);
            _actorRotateGizmo.RefreshRings();
            _actorRotateProjectionCached = true;
            _actorRotateProjectionPivot = pivot;
            _actorRotateProjectionAxes = axes;
            _actorRotateProjectionCameraTransform = cameraTransform;
            _actorRotateProjectionViewportSize = viewportSize;
            _actorRotateProjectionOrigin = originScreen;
            _actorRotateProjectionFov = _camera.Fov;
            _actorRotateProjectionWorldLength = worldLength;
        }
        _actorRotateGizmo.SetHoveredAxis(
            _actorRotateGizmo.TryPickAxis(GetViewport().GetMousePosition(), out LegacyActorMoveGizmoAxis hoveredAxis)
                ? hoveredAxis
                : LegacyActorMoveGizmoAxis.None);
    }

    private void ProjectActorRotateRing(
        LegacyActorMoveGizmoAxis axis,
        Vector3 pivot,
        Vector3 firstDirection,
        Vector3 secondDirection,
        float radius)
    {
        for (int index = 0; index <= LegacyActorRotateGizmoControl.RingSegments; index++)
        {
            float angle = Mathf.Tau * index / LegacyActorRotateGizmoControl.RingSegments;
            Vector3 point = pivot + radius * (firstDirection * Mathf.Cos(angle) + secondDirection * Mathf.Sin(angle));
            _actorRotateGizmo.SetRingPoint(axis, index, _camera.UnprojectPosition(point));
        }
    }

    private Vector3 GetActorRotateAxisDirection(LegacyActorMoveGizmoAxis axis)
    {
        Vector3 localDirection = axis switch
        {
            LegacyActorMoveGizmoAxis.X => Vector3.Right,
            LegacyActorMoveGizmoAxis.Y => Vector3.Up,
            LegacyActorMoveGizmoAxis.Z => Basis.Identity.Z,
            _ => Vector3.Zero
        };
        if (_actorRotateUsesLocalSpace && _selectedActor is not null)
            localDirection = _selectedActor.GlobalBasis.Orthonormalized() * localDirection;
        return localDirection.Normalized();
    }

    private void BeginActorRotate(Vector2 screenPosition, LegacyActorMoveGizmoAxis axis)
    {
        if (!_actorRotateToolActive
            || _document is null
            || _playMode
            || _selectedActor is null
            || _selectedActors.Count == 0
            || _actorMovePending)
            return;

        _actorRotateSelectedActors = _selectedActors
            .Where(_actors.Contains)
            .OrderBy(GetActorHierarchyDepth)
            .ToArray();
        if (_actorRotateSelectedActors.Length == 0 || !_actorRotateSelectedActors.Contains(_selectedActor))
        {
            ClearActorRotateState();
            return;
        }

        _actorRotateConstraintAxis = axis;
        _actorRotateUsesLocalSpace = _actorMoveLocalSpace && _actorRotateSelectedActors.Length == 1;
        _actorRotateAxisDirection = GetActorRotateAxisDirection(axis);
        _actorRotatePivot = Vector3.Zero;
        foreach (WorldActorNode actor in _actorRotateSelectedActors)
            _actorRotatePivot += actor.GlobalPosition;
        _actorRotatePivot /= _actorRotateSelectedActors.Length;
        _actorRotateStartPointer = screenPosition;
        if (!TryGetActorRotatePlanePoint(screenPosition, out Vector3 startPoint))
        {
            ClearActorRotateState();
            return;
        }

        Vector3 startRadial = startPoint - _actorRotatePivot;
        if (startRadial.LengthSquared() < 0.0625f)
        {
            ClearActorRotateState();
            return;
        }

        _actorRotateLastRadialDirection = startRadial.Normalized();
        _actorRotateAccumulatedRadians = 0f;
        _actorRotateAffectedActors = _actors
            .Where(candidate => _actorRotateSelectedActors.Any(selected =>
                candidate == selected || selected.IsAncestorOf(candidate)))
            .OrderBy(GetActorHierarchyDepth)
            .ToArray();
        _actorRotateBefore = CaptureActorMoveStates(_actorRotateAffectedActors);
        _actorRotatePending = true;
        _actorRotateDragging = false;
    }

    private bool HandleActorRotateInput(InputEvent inputEvent)
    {
        if (!_actorRotatePending)
            return false;

        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            FinishActorRotate();
            GetViewport().SetInputAsHandled();
            return true;
        }

        if (inputEvent is InputEventMouseMotion motion)
        {
            UpdateActorRotateFromScreen(motion.Position, motion.CtrlPressed || motion.MetaPressed);
            GetViewport().SetInputAsHandled();
            return true;
        }

        if (inputEvent is InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false })
        {
            CancelActorRotate();
            GetViewport().SetInputAsHandled();
            return true;
        }

        return false;
    }

    private void UpdateActorRotateFromScreen(Vector2 screenPosition, bool snapToLegacyOrientation)
    {
        if (!_actorRotatePending || _actorRotateBefore is null)
            return;
        if (!_actorRotateDragging)
        {
            if (screenPosition.DistanceSquaredTo(_actorRotateStartPointer) < 16f)
                return;
            _actorRotateDragging = true;
            string axisName = _actorRotateConstraintAxis switch
            {
                LegacyActorMoveGizmoAxis.X => "X",
                LegacyActorMoveGizmoAxis.Y => "Y",
                LegacyActorMoveGizmoAxis.Z => "Z",
                _ => ""
            };
            _status.Text = $"Drag to rotate around {(_actorRotateUsesLocalSpace ? "local" : "world")} {axisName}; hold Ctrl/Cmd to snap. Press Esc to cancel.";
        }

        if (!TryGetActorRotatePlanePoint(screenPosition, out Vector3 currentPoint))
            return;
        Vector3 radial = currentPoint - _actorRotatePivot;
        if (radial.LengthSquared() < 0.0625f)
            return;
        Vector3 currentDirection = radial.Normalized();
        float angleDelta = Mathf.Atan2(
            _actorRotateAxisDirection.Dot(_actorRotateLastRadialDirection.Cross(currentDirection)),
            _actorRotateLastRadialDirection.Dot(currentDirection));
        _actorRotateAccumulatedRadians += angleDelta;
        _actorRotateLastRadialDirection = currentDirection;

        bool shouldSnap = snapToLegacyOrientation || _actorRotateSnap;
        Quaternion rotationDelta = new Quaternion(_actorRotateAxisDirection, _actorRotateAccumulatedRadians).Normalized();
        foreach (WorldActorNode actor in _actorRotateSelectedActors)
        {
            if (!GodotObject.IsInstanceValid(actor)
                || !_actorRotateBefore.TryGetValue(actor.LegacyName, out ActorMoveSnapshot? state)
                || state is null)
                continue;

            Quaternion rotation = (rotationDelta * state.Transform.Basis.Orthonormalized().GetRotationQuaternion()).Normalized();
            if (shouldSnap && _actorRotateSelectedActors.Length == 1)
                rotation = SnapActorRotationToLegacyOrientations(rotation);
            Basis basis = new Basis(rotation).Scaled(state.Transform.Basis.Scale);
            Vector3 position = _actorRotatePivot + rotationDelta * (state.Transform.Origin - _actorRotatePivot);
            actor.GlobalTransform = new Transform3D(basis, position);
            actor.AngularVelocity = Vector3.Zero;
        }
        SaveActorMoveTransforms(_actorRotateAffectedActors);
    }

    private bool TryGetActorRotatePlanePoint(Vector2 screenPosition, out Vector3 point)
    {
        Vector3 rayOrigin = _camera.ProjectRayOrigin(screenPosition);
        Vector3 rayDirection = _camera.ProjectRayNormal(screenPosition);
        float denominator = _actorRotateAxisDirection.Dot(rayDirection);
        if (Mathf.Abs(denominator) < 0.0001f)
        {
            point = Vector3.Zero;
            return false;
        }

        float distance = _actorRotateAxisDirection.Dot(_actorRotatePivot - rayOrigin) / denominator;
        if (distance < 0f)
        {
            point = Vector3.Zero;
            return false;
        }

        point = rayOrigin + rayDirection * distance;
        return true;
    }

    private void FinishActorRotate()
    {
        if (!_actorRotatePending)
            return;

        Dictionary<string, ActorMoveSnapshot>? before = _actorRotateBefore;
        bool rotated = _actorRotateDragging && before is not null;
        WorldActorNode[] affectedActors = _actorRotateAffectedActors;
        int selectedCount = _actorRotateSelectedActors.Length;
        ClearActorRotateState();
        if (!rotated || before is null)
            return;

        if (_autoSetSpawnOnRotate)
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

        RecordHistory("Rotate",
            () => ApplyActorMoveStates(before),
            () => ApplyActorMoveStates(after));
        _status.Text = $"Rotated {selectedCount} actor{(selectedCount == 1 ? "" : "s")}.";
    }

    private void CancelActorRotate()
    {
        if (!_actorRotatePending)
            return;

        Dictionary<string, ActorMoveSnapshot>? before = _actorRotateBefore;
        bool wasDragging = _actorRotateDragging;
        ClearActorRotateState();
        if (wasDragging && before is not null)
        {
            ApplyActorMoveStates(before);
            _status.Text = "Actor rotation canceled.";
        }
    }

    private void ClearActorRotateState()
    {
        _actorRotatePending = false;
        _actorRotateDragging = false;
        _actorRotateUsesLocalSpace = false;
        _actorRotateSelectedActors = Array.Empty<WorldActorNode>();
        _actorRotateAffectedActors = Array.Empty<WorldActorNode>();
        _actorRotateBefore = null;
        _actorRotateConstraintAxis = LegacyActorMoveGizmoAxis.None;
        _actorRotateAxisDirection = Vector3.Zero;
        _actorRotateLastRadialDirection = Vector3.Zero;
        _actorRotateAccumulatedRadians = 0f;
    }

    private static Quaternion SnapActorRotationToLegacyOrientations(Quaternion rotation)
    {
        rotation = rotation.Normalized();
        Vector3 forward = RoundRotationVector(rotation * Vector3.Forward);
        Vector3 up = RoundRotationVector(rotation * Vector3.Up);
        if (forward.LengthSquared() < 0.5f)
            return rotation;
        forward = forward.Normalized();
        up -= forward * up.Dot(forward);
        if (up.LengthSquared() < 0.01f)
        {
            Vector3 reference = Mathf.Abs(forward.Dot(Vector3.Up)) < 0.95f ? Vector3.Up : Vector3.Right;
            up = reference - forward * reference.Dot(forward);
        }
        up = up.Normalized();
        Vector3 right = forward.Cross(up).Normalized();
        up = right.Cross(forward).Normalized();
        return new Basis(right, up, -forward).GetRotationQuaternion().Normalized();
    }

    private static Vector3 RoundRotationVector(Vector3 vector)
        => new(Mathf.Round(vector.X), Mathf.Round(vector.Y), Mathf.Round(vector.Z));

    private void SetActorRotateToolPreference(bool enabled)
    {
        _actorRotateToolActive = enabled;
        if (_actorRotateToolToggle is not null
            && IsInstanceValid(_actorRotateToolToggle)
            && _actorRotateToolToggle.ButtonPressed != enabled)
            _actorRotateToolToggle.SetPressedNoSignal(enabled);
        UpdateActorMoveGizmo();
        UpdateActorRotateGizmo();
    }

    private void SetActorRotateSnapPreference(bool enabled)
    {
        _actorRotateSnap = enabled;
        if (_actorRotateSnapToggle is not null
            && IsInstanceValid(_actorRotateSnapToggle)
            && _actorRotateSnapToggle.ButtonPressed != enabled)
            _actorRotateSnapToggle.SetPressedNoSignal(enabled);
        SaveEditorPreferences();
    }
}
