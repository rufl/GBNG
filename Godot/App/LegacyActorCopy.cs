using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GBNG.Assets;
using GBNG.Domain;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private const float CopySnapHorizontal = 1.25f;
    private const float CopySnapVertical = 0.75f;
    private readonly List<WorldActorNode> _copySources = new();
    private WorldActorNode? _copyFocusActor;
    private Node3D? _copyPreviewRoot;
    private StandardMaterial3D? _copyPreviewMaterial;
    private bool _copyPlacementActive;

    private sealed record CopyActorSnapshot(string ActorName, string ActorJson, int Index);

    internal bool IsCopyPlacementActive => _copyPlacementActive;
    internal Node3D? CopyPreviewRoot => _copyPreviewRoot;

    private void BeginCopyPlacement()
    {
        if (_copyPlacementActive || _document is null || _playMode)
            return;
        if (_selectedActor is null)
        {
            _status.Text = "Select an actor before copying.";
            return;
        }

        _copySources.Clear();
        var includedNames = new HashSet<string>(StringComparer.Ordinal) { _selectedActor.LegacyName };
        _copySources.Add(_selectedActor);
        for (int index = 0; index < _copySources.Count; index++)
        {
            string parentName = _copySources[index].LegacyName;
            foreach (WorldActorNode candidate in _actors)
            {
                if (includedNames.Contains(candidate.LegacyName) || candidate.State.TransformParent != parentName)
                    continue;
                includedNames.Add(candidate.LegacyName);
                _copySources.Add(candidate);
            }
        }

        if (_lanSession.HasPendingAssetSync)
        {
            _copySources.Clear();
            _status.Text = "Wait for project assets to finish syncing before copying actors.";
            return;
        }
        if (_lanSession.IsClient && _copySources.Any(actor => _unsharedAssetUris.Contains(actor.RenderableUri)))
        {
            _copySources.Clear();
            _status.Text = "This actor uses an asset that is not synchronized with the host yet.";
            return;
        }

        foreach (WorldActorNode source in _copySources)
            source.SaveTransform();
        _copyFocusActor = _selectedActor;
        _copyPlacementActive = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        BuildCopyPreview();
        UpdateCopyPreview(GetViewport().GetMousePosition(), snapToGrid: false);
        _status.Text = "Copy mode — move the cursor and click to place; hold Ctrl/Cmd to snap, or press Esc to cancel.";
    }

    private void BuildCopyPreview()
    {
        if (_copyFocusActor is null)
            return;

        _copyPreviewMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.08f, 0.95f, 1f, 0.72f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            EmissionEnabled = true,
            Emission = new Color(0.08f, 0.65f, 0.9f)
        };
        _copyPreviewRoot = new Node3D { Name = "CopyPlacementPreview" };
        _world.AddChild(_copyPreviewRoot);
        Quaternion focusRotation = _copyFocusActor.GlobalBasis.GetRotationQuaternion();
        _copyPreviewRoot.GlobalTransform = new Transform3D(new Basis(focusRotation), _copyFocusActor.GlobalPosition);
        Transform3D inverseRoot = _copyPreviewRoot.GlobalTransform.AffineInverse();

        for (int index = 0; index < _copySources.Count; index++)
        {
            WorldActorNode source = _copySources[index];
            var previewActor = new Node3D { Name = $"Preview_{index}" };
            _copyPreviewRoot.AddChild(previewActor);
            previewActor.Transform = inverseRoot * source.GlobalTransform;

            Node3D? sourceRenderable = source.GetNodeOrNull<Node3D>("Renderable");
            if (sourceRenderable is null
                || !_catalog.TryGet(source.RenderableUri, out RenderableEntry? entry))
                continue;

            string? modelPath = entry.SelectPrimaryModel();
            Node3D? renderableCopy = modelPath is null
                ? null
                : LegacyRenderableCatalog.LoadModelInstance(modelPath);
            if (renderableCopy is null)
                continue;

            renderableCopy.Name = "Renderable";
            renderableCopy.Transform = sourceRenderable.Transform;
            previewActor.AddChild(renderableCopy);
            if (renderableCopy is GeometryInstance3D rootGeometry)
                SetCopyPreviewMaterial(rootGeometry);
            foreach (Node node in renderableCopy.FindChildren("*", "GeometryInstance3D", true, false))
            {
                if (node is GeometryInstance3D geometry)
                    SetCopyPreviewMaterial(geometry);
            }
        }
    }

    private void SetCopyPreviewMaterial(GeometryInstance3D geometry)
    {
        geometry.MaterialOverride = _copyPreviewMaterial;
        geometry.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
    }

    private void UpdateCopyPreview(Vector2 screenPosition, bool snapToGrid)
    {
        if (!_copyPlacementActive || _copyPreviewRoot is null || _copyFocusActor is null)
            return;

        Vector3 rayOrigin = _camera.ProjectRayOrigin(screenPosition);
        Vector3 rayDirection = _camera.ProjectRayNormal(screenPosition);
        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(
            rayOrigin,
            rayOrigin + rayDirection * 10_000f,
            collisionMask: 3u);
        query.CollideWithAreas = false;
        Godot.Collections.Dictionary hit = _world.GetWorld3D().DirectSpaceState.IntersectRay(query);
        Vector3 position;
        if (hit.Count > 0)
        {
            position = hit["position"].AsVector3();
        }
        else if (Math.Abs(rayDirection.Y) > 0.0001f)
        {
            float distance = -rayOrigin.Y / rayDirection.Y;
            position = distance >= 0f ? rayOrigin + rayDirection * distance : _copyFocusActor.GlobalPosition;
        }
        else
        {
            position = _copyFocusActor.GlobalPosition;
        }

        if (snapToGrid)
        {
            position.X = Mathf.Round(position.X / CopySnapHorizontal) * CopySnapHorizontal;
            position.Y = Mathf.Round(position.Y / CopySnapVertical) * CopySnapVertical;
            position.Z = Mathf.Round(position.Z / CopySnapHorizontal) * CopySnapHorizontal;
        }
        _copyPreviewRoot.GlobalPosition = position;
    }

    private void PasteCopyAtCursor(Vector2 screenPosition, bool snapToGrid)
    {
        UpdateCopyPreview(screenPosition, snapToGrid);
        if (_copyPreviewRoot is not null)
            PasteCopyAt(_copyPreviewRoot.GlobalPosition);
    }

    internal void PasteCopyAt(Vector3 pastedFocusPosition)
    {
        if (!_copyPlacementActive || _document is null || _copyFocusActor is null || _copySources.Count == 0)
            return;
        if (!_actors.Contains(_copyFocusActor))
        {
            CancelCopyPlacement();
            return;
        }

        Vector3 offset = pastedFocusPosition - _copyFocusActor.GlobalPosition;
        var newNames = new Dictionary<string, string>(_copySources.Count, StringComparer.Ordinal);
        foreach (WorldActorNode source in _copySources)
            newNames.Add(source.LegacyName, Guid.NewGuid().ToString("N"));

        var snapshots = new List<CopyActorSnapshot>(_copySources.Count);
        int insertIndex = _document.Actors.Count;
        foreach (WorldActorNode source in _copySources)
        {
            source.SaveTransform();
            var cloneData = (JsonObject)source.State.Data.DeepClone();
            string rootName = LegacySaveDocument.ReadString(cloneData["cloneParent"]);
            if (rootName.Length == 0)
                rootName = source.LegacyName;
            cloneData["name"] = newNames[source.LegacyName];
            cloneData["displayName"] = GetCopyDisplayName(rootName);
            cloneData["cloneParent"] = rootName;

            Transform3D pastedTransform = source.GlobalTransform;
            pastedTransform.Origin += offset;
            new LegacyActorData(cloneData).SetWorldTransform(pastedTransform);

            string transformParent = LegacySaveDocument.ReadString(cloneData["transformParent"]);
            if (newNames.TryGetValue(transformParent, out string? pastedParent))
                cloneData["transformParent"] = pastedParent;
            string spawnTransformParent = LegacySaveDocument.ReadString(cloneData["spawnTransformParent"]);
            if (newNames.TryGetValue(spawnTransformParent, out string? pastedSpawnParent))
                cloneData["spawnTransformParent"] = pastedSpawnParent;

            snapshots.Add(new CopyActorSnapshot(
                newNames[source.LegacyName],
                cloneData.ToJsonString(),
                insertIndex++));
        }

        foreach (CopyActorSnapshot snapshot in snapshots)
            RestoreActorFromJson(snapshot.ActorJson, snapshot.Index);

        CopyActorSnapshot[] history = snapshots.ToArray();
        RecordHistory(
            history.Length == 1 ? $"Paste {GetDisplayName(history[0].ActorName)}" : $"Paste {history.Length} actors",
            () =>
            {
                for (int index = history.Length - 1; index >= 0; index--)
                    RemoveActorFromWorld(history[index].ActorName);
            },
            () =>
            {
                foreach (CopyActorSnapshot snapshot in history)
                    RestoreActorFromJson(snapshot.ActorJson, snapshot.Index);
            });
        _status.Text = $"Placed {history.Length} actor copies. Click to place another or press Esc to finish.";
    }

    private string GetCopyDisplayName(string rootName)
    {
        WorldActorNode? root = _actors.FirstOrDefault(actor => actor.LegacyName == rootName);
        string baseDisplayName = root?.State.DisplayName ?? rootName;
        int copies = _document!.Actors.Count(actor => actor.Name != rootName
            && LegacySaveDocument.ReadString(actor.Data["cloneParent"]) == rootName);
        return $"{baseDisplayName}-{copies + 1}";
    }

    private string GetDisplayName(string actorName)
        => _actors.FirstOrDefault(actor => actor.LegacyName == actorName)?.State.DisplayName ?? actorName;

    private void CancelCopyPlacement(bool showStatus = true)
    {
        if (_copyPreviewRoot is not null && GodotObject.IsInstanceValid(_copyPreviewRoot))
            _copyPreviewRoot.QueueFree();
        _copyPreviewRoot = null;
        _copyPreviewMaterial = null;
        _copyFocusActor = null;
        _copySources.Clear();
        bool wasActive = _copyPlacementActive;
        _copyPlacementActive = false;
        if (wasActive)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
            if (showStatus && _status is not null)
                _status.Text = "Copy mode canceled.";
        }
    }
}
