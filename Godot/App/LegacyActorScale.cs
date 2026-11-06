using System;
using System.Collections.Generic;
using System.Linq;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private const float ScaleMinimum = 0.1f;
    private const float ScaleUndoThreshold = 0.001f;
    private const float ScaleDragSensitivity = 0.25f * 0.3f * 0.1f;
    private const float ScaleDragSensitivityWithModifier = 0.25f * 0.7f * 0.1f;
    private LegacyActorScaleGizmoControl _actorScaleGizmo = null!;
    private CheckBox _actorScaleToolToggle = null!;
    private bool _actorScaleToolActive;
    private bool _actorScalePending;
    private bool _actorScaleDragging;
    private WorldActorNode[] _actorScaleSelectedActors = Array.Empty<WorldActorNode>();
    private WorldActorNode[] _actorScaleAffectedActors = Array.Empty<WorldActorNode>();
    private Dictionary<string, ActorMoveSnapshot>? _actorScaleBefore;
    private readonly Dictionary<string, Vector3> _actorScaleStartScales = new(StringComparer.Ordinal);

    private void UpdateActorScaleGizmo()
    {
        if (_actorScaleGizmo is null || !IsInstanceValid(_actorScaleGizmo))
            return;

        bool canShow = _actorScaleToolActive
            && !_actorRotateToolActive
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
            _actorScaleGizmo.SetHandle(false, Vector2.Zero, 0f);
            return;
        }

        Vector3 pivot = _selectedActor!.GetWorldRenderBounds().GetCenter();
        if (_camera.ToLocal(pivot).Z >= 0f)
        {
            _actorScaleGizmo.SetHandle(false, Vector2.Zero, 0f);
            return;
        }

        Vector2 screenPosition = _camera.UnprojectPosition(pivot);
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        if (!GetViewport().GetVisibleRect().HasPoint(screenPosition))
        {
            _actorScaleGizmo.SetHandle(false, Vector2.Zero, 0f);
            return;
        }

        float viewportHeight = Mathf.Max(viewportSize.Y, 1f);
        float projectedRadius = 0.075f
            * Mathf.Cos(Mathf.DegToRad(_camera.Fov) * 0.5f)
            * viewportHeight;
        float radius = Mathf.Clamp(projectedRadius, 18f, 72f);
        _actorScaleGizmo.SetHandle(true, screenPosition, radius);
        _actorScaleGizmo.SetHovered(_actorScaleGizmo.TryPick(GetViewport().GetMousePosition()));
    }

    private bool HandleActorScaleInput(InputEvent inputEvent)
    {
        if (!_actorScalePending)
            return false;

        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            FinishActorScale();
            GetViewport().SetInputAsHandled();
            return true;
        }

        if (inputEvent is InputEventMouseMotion motion)
        {
            UpdateActorScaleFromMotion(motion);
            GetViewport().SetInputAsHandled();
            return true;
        }

        if (inputEvent is InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false })
        {
            CancelActorScale();
            GetViewport().SetInputAsHandled();
            return true;
        }

        return false;
    }

    private void BeginActorScale()
    {
        if (!_actorScaleToolActive
            || _document is null
            || _playMode
            || _selectedActor is null
            || _selectedActors.Count == 0
            || _actorMovePending
            || _actorRotatePending)
            return;

        _actorScaleSelectedActors = _selectedActors
            .Where(_actors.Contains)
            .OrderBy(GetActorHierarchyDepth)
            .ToArray();
        if (_actorScaleSelectedActors.Length == 0 || !_actorScaleSelectedActors.Contains(_selectedActor))
        {
            ClearActorScaleState();
            return;
        }

        _actorScaleStartScales.Clear();
        foreach (WorldActorNode actor in _actorScaleSelectedActors)
            _actorScaleStartScales.Add(actor.LegacyName, actor.Scale);
        _actorScaleAffectedActors = _actors
            .Where(candidate => _actorScaleSelectedActors.Any(selected =>
                candidate == selected || selected.IsAncestorOf(candidate)))
            .OrderBy(GetActorHierarchyDepth)
            .ToArray();
        _actorScaleBefore = CaptureActorMoveStates(_actorScaleAffectedActors);
        _actorScalePending = true;
        _actorScaleDragging = false;
    }

    private void UpdateActorScaleFromMotion(InputEventMouseMotion motion)
    {
        if (!_actorScalePending || _actorScaleBefore is null)
            return;
        if (!_actorScaleDragging)
        {
            _actorScaleDragging = true;
            _status.Text = "Drag to scale uniformly; hold Ctrl/Cmd to scale faster. Press Esc to cancel.";
        }

        float sensitivity = motion.CtrlPressed || motion.MetaPressed
            ? ScaleDragSensitivityWithModifier
            : ScaleDragSensitivity;
        float factor = 1f + (motion.Relative.X + motion.Relative.Y) * sensitivity;
        foreach (WorldActorNode actor in _actorScaleSelectedActors)
        {
            if (!GodotObject.IsInstanceValid(actor)
                || !_actors.Contains(actor)
                || !_actorScaleBefore.ContainsKey(actor.LegacyName))
                continue;

            Vector3 scale = actor.Scale * factor;
            if (scale.X < ScaleMinimum)
                scale = Vector3.One * ScaleMinimum;
            actor.Scale = scale;
            actor.State.SetScale(scale);
        }
        SaveActorMoveTransforms(_actorScaleAffectedActors);
        RefreshActorScaleFields();
        UpdateActorScaleGizmo();
    }

    private void RefreshActorScaleFields()
    {
        if (_selectedActor is null || _scaleFields.Length != 3)
            return;

        bool wasUpdatingInspector = _updatingInspector;
        _updatingInspector = true;
        try
        {
            for (int axis = 0; axis < 3; axis++)
                _scaleFields[axis].Value = _selectedActor.Scale[axis];
        }
        finally
        {
            _updatingInspector = wasUpdatingInspector;
        }
    }

    private void FinishActorScale()
    {
        if (!_actorScalePending)
            return;

        Dictionary<string, ActorMoveSnapshot>? before = _actorScaleBefore;
        WorldActorNode[] selectedActors = _actorScaleSelectedActors;
        WorldActorNode[] affectedActors = _actorScaleAffectedActors;
        int selectedCount = selectedActors.Length;
        bool wasDragging = _actorScaleDragging;
        bool changed = wasDragging && HasActorScaleChange();
        ClearActorScaleState();
        if (before is null)
            return;
        if (!changed)
        {
            if (wasDragging)
                ApplyActorMoveStates(before);
            return;
        }

        SaveActorMoveTransforms(affectedActors);
        var updatedRoots = new HashSet<WorldActorNode>();
        foreach (WorldActorNode actor in selectedActors)
        {
            if (GodotObject.IsInstanceValid(actor) && _actors.Contains(actor))
            {
                WorldActorNode root = GetActorFamilyRoot(actor);
                if (updatedRoots.Add(root))
                    UpdateActorFamilySpawnTransformsForRoot(root);
            }
        }

        Dictionary<string, ActorMoveSnapshot> after = CaptureActorMoveStates(affectedActors);
        if (ActorMoveTransformsEqual(before, after))
        {
            ApplyActorMoveStates(before);
            return;
        }

        RecordHistory("Scale",
            () => ApplyActorMoveStates(before),
            () => ApplyActorMoveStates(after));
        _status.Text = $"Scaled {selectedCount} actor{(selectedCount == 1 ? "" : "s")}.";
    }

    private bool HasActorScaleChange()
    {
        foreach (WorldActorNode actor in _actorScaleSelectedActors)
        {
            if (GodotObject.IsInstanceValid(actor)
                && _actorScaleStartScales.TryGetValue(actor.LegacyName, out Vector3 before)
                && actor.Scale.DistanceTo(before) > ScaleUndoThreshold)
                return true;
        }
        return false;
    }

    private void CancelActorScale()
    {
        if (!_actorScalePending)
            return;

        Dictionary<string, ActorMoveSnapshot>? before = _actorScaleBefore;
        bool wasDragging = _actorScaleDragging;
        ClearActorScaleState();
        if (wasDragging && before is not null)
        {
            ApplyActorMoveStates(before);
            _status.Text = "Actor scaling canceled.";
        }
    }

    private void ClearActorScaleState()
    {
        _actorScalePending = false;
        _actorScaleDragging = false;
        _actorScaleSelectedActors = Array.Empty<WorldActorNode>();
        _actorScaleAffectedActors = Array.Empty<WorldActorNode>();
        _actorScaleBefore = null;
        _actorScaleStartScales.Clear();
    }

    private void SetActorScaleToolPreference(bool enabled)
    {
        if (enabled)
        {
            if (_actorRotateToolActive)
                SetActorRotateToolPreference(false);
            if (_actorMovePending)
                CancelActorMove();
            if (_actorRotatePending)
                CancelActorRotate();
        }
        else if (_actorScalePending)
        {
            CancelActorScale();
        }

        _actorScaleToolActive = enabled;
        if (_actorScaleToolToggle is not null
            && IsInstanceValid(_actorScaleToolToggle)
            && _actorScaleToolToggle.ButtonPressed != enabled)
            _actorScaleToolToggle.SetPressedNoSignal(enabled);
        UpdateActorMoveGizmo();
        UpdateActorRotateGizmo();
        UpdateActorScaleGizmo();
    }
}
