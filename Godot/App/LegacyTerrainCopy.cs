using System.Collections.Generic;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private bool _terrainSelectionDragging;
    private Vector3I _terrainSelectionStartCell;
    private Vector3I _terrainSelectionEndCell;
    private Vector3I _terrainSelectionOriginCell;
    private LegacyTerrainData? _terrainSelectionData;
    private LegacyTerrainNode? _terrainSelectionPreview;
    private LegacyTerrainNode? _terrainCopyPreview;
    private bool _terrainCopyActive;
    private Vector3I _terrainCopyTargetOrigin;
    private int _terrainSelectionCellCount;

    private bool IsTerrainSelectionModeActive
        => IsTerrainBrushActive && _terrainEditorWindow!.Brush.Mode == LegacyTerrainBrushMode.Select;

    private void BeginTerrainSelectionAtScreen(Vector2 screenPosition)
    {
        if (!TryGetTerrainSelectionCellAtScreen(screenPosition, out Vector3I cell))
            return;

        CancelTerrainCopy();
        _terrainSelectionDragging = true;
        _terrainSelectionStartCell = cell;
        _terrainSelectionEndCell = cell;
        _status.Text = "Drag over terrain to select a cell region.";
    }

    private void UpdateTerrainSelectionAtScreen(Vector2 screenPosition)
    {
        if (_terrainSelectionDragging
            && TryGetTerrainSelectionCellAtScreen(screenPosition, out Vector3I cell))
            _terrainSelectionEndCell = cell;
    }

    private void CompleteTerrainSelectionAtScreen(Vector2 screenPosition)
    {
        if (!_terrainSelectionDragging)
            return;
        UpdateTerrainSelectionAtScreen(screenPosition);
        _terrainSelectionDragging = false;
        SelectTerrainCells(_terrainSelectionStartCell, _terrainSelectionEndCell);
    }

    private void CancelTerrainSelectionDrag()
    {
        _terrainSelectionDragging = false;
    }

    private bool TryGetTerrainSelectionCellAtScreen(Vector2 screenPosition, out Vector3I cell)
    {
        cell = default;
        if (_terrain is null)
            return false;

        Vector3 rayOrigin = _camera.ProjectRayOrigin(screenPosition);
        Vector3 rayEnd = rayOrigin + _camera.ProjectRayNormal(screenPosition) * 1000f;
        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayEnd, 2u);
        query.CollideWithAreas = false;
        query.CollideWithBodies = true;
        Godot.Collections.Dictionary hit = _world.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node collider || !IsTerrainCollider(collider))
            return false;

        if (!_terrain.TryGetCellForHit(hit["position"].AsVector3(), hit["normal"].AsVector3(), out int x, out int y, out int z)
            || !_terrain.TryGetCell(x, y, z, out _, out _, out _))
            return false;
        cell = new Vector3I(x, y, z);
        return true;
    }

    private bool SelectTerrainCells(Vector3I first, Vector3I last)
    {
        if (_terrain is null)
            return false;

        int minX = Mathf.Min(first.X, last.X);
        int minY = Mathf.Min(first.Y, last.Y);
        int minZ = Mathf.Min(first.Z, last.Z);
        int maxX = Mathf.Max(first.X, last.X);
        int maxY = Mathf.Max(first.Y, last.Y);
        int maxZ = Mathf.Max(first.Z, last.Z);
        var selectionData = LegacyTerrainData.CreateEmpty(maxX - minX + 1, maxY - minY + 1, maxZ - minZ + 1);
        int selectedCellCount = 0;
        for (int y = minY; y <= maxY; y++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!_terrain.TryGetCell(x, y, z, out byte shape, out ushort style, out byte direction))
                        continue;
                    selectionData.SetCell(x - minX, y - minY, z - minZ, shape, style, direction);
                    selectedCellCount++;
                }
            }
        }

        ClearTerrainSelection();
        if (selectedCellCount == 0)
        {
            _status.Text = "The selected region contains no terrain cells.";
            return false;
        }

        _terrainSelectionOriginCell = new Vector3I(minX, minY, minZ);
        _terrainSelectionData = selectionData;
        _terrainSelectionCellCount = selectedCellCount;
        _terrainSelectionPreview = LegacyTerrainNode.CreatePreview(selectionData, LoadCustomTerrainStyleTextures());
        _terrainSelectionPreview.Name = "TerrainSelectionPreview";
        _world.AddChild(_terrainSelectionPreview);
        PositionTerrainPreview(_terrainSelectionPreview, selectionData, _terrainSelectionOriginCell);
        RefreshTerrainSelectionStatus();
        _status.Text = $"Selected {selectedCellCount} terrain cells.";
        return true;
    }

    private void BeginTerrainCopy()
    {
        if (_terrain is null || _terrainSelectionData is null || _terrainSelectionCellCount == 0)
        {
            _status.Text = "Select terrain cells before copying.";
            return;
        }

        CancelCopyPlacement(showStatus: false);
        CancelTerrainCopy();
        _terrainCopyActive = true;
        _terrainCopyTargetOrigin = _terrainSelectionOriginCell;
        _terrainCopyPreview = LegacyTerrainNode.CreatePreview(_terrainSelectionData, LoadCustomTerrainStyleTextures());
        _world.AddChild(_terrainCopyPreview);
        PositionTerrainPreview(_terrainCopyPreview, _terrainSelectionData, _terrainCopyTargetOrigin);
        RefreshTerrainSelectionStatus();
        _status.Text = "Terrain copy preview active — move the cursor and click to paste; Esc cancels.";
    }

    private void UpdateTerrainCopyPreviewAtScreen(Vector2 screenPosition)
    {
        if (!_terrainCopyActive || _terrainSelectionData is null || _terrain is null || _terrainCopyPreview is null)
            return;
        if (!TryGetTerrainPlacementCellAtScreen(screenPosition, out Vector3I targetOrigin))
            return;
        SetTerrainCopyTargetOrigin(targetOrigin);
    }

    private bool TryGetTerrainPlacementCellAtScreen(Vector2 screenPosition, out Vector3I cell)
    {
        cell = default;
        if (_terrain is null)
            return false;

        Vector3 rayOrigin = _camera.ProjectRayOrigin(screenPosition);
        Vector3 rayEnd = rayOrigin + _camera.ProjectRayNormal(screenPosition) * 1000f;
        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayEnd, 2u);
        query.CollideWithAreas = false;
        query.CollideWithBodies = true;
        Godot.Collections.Dictionary hit = _world.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node collider)
            return false;

        bool terrainHit = IsTerrainCollider(collider);
        if (!terrainHit && collider != _stageGroundBody)
            return false;

        Vector3 position = hit["position"].AsVector3();
        Vector3 normal = hit["normal"].AsVector3();
        if (_terrain.TryGetEmptyPlacementCell(position, normal, out int x, out int y, out int z))
        {
            cell = new Vector3I(x, y, z);
            return true;
        }
        if (!terrainHit || !_terrain.TryGetCellForHit(position, normal, out x, out y, out z))
            return false;
        cell = new Vector3I(x, y, z);
        return true;
    }

    private void SetTerrainCopyTargetOrigin(Vector3I targetOrigin)
    {
        if (_terrainSelectionData is null || _terrainCopyPreview is null)
            return;
        _terrainCopyTargetOrigin = targetOrigin;
        PositionTerrainPreview(_terrainCopyPreview, _terrainSelectionData, targetOrigin);
    }

    private void PositionTerrainPreview(LegacyTerrainNode preview, LegacyTerrainData previewData, Vector3I originCell)
    {
        if (_terrain is null)
            return;
        Basis basis = _terrain.GlobalBasis;
        Vector3 targetCenter = _terrain.GetCellCenterGlobal(originCell.X, originCell.Y, originCell.Z);
        Vector3 previewOriginCenter = LegacyTerrainNode.GetCellCenterLocal(previewData, 0, 0, 0);
        preview.GlobalBasis = basis;
        preview.GlobalPosition = targetCenter - basis * previewOriginCenter;
    }

    private void PasteTerrainCopyAtScreen(Vector2 screenPosition)
    {
        if (!TryGetTerrainPlacementCellAtScreen(screenPosition, out Vector3I targetOrigin))
        {
            _status.Text = "Place the terrain copy over the terrain or map ground.";
            return;
        }
        SetTerrainCopyTargetOrigin(targetOrigin);
        PasteTerrainCopyAtOrigin(targetOrigin);
    }

    private bool PasteTerrainCopyAtOrigin(Vector3I targetOrigin)
    {
        if (_terrain is null || _terrainSelectionData is null || _terrainSelectionCellCount == 0)
            return false;

        LegacyTerrainData data = _terrainSelectionData;
        if (targetOrigin.X < 0 || targetOrigin.Y < 0 || targetOrigin.Z < 0
            || targetOrigin.X + data.Width > _terrain.Data.Width
            || targetOrigin.Y + data.Height > _terrain.Data.Height
            || targetOrigin.Z + data.Depth > _terrain.Data.Depth)
        {
            _status.Text = "The terrain copy does not fit inside the map bounds.";
            return false;
        }

        var before = new Dictionary<Vector3I, TerrainCellSnapshot>(_terrainSelectionCellCount);
        var after = new Dictionary<Vector3I, TerrainCellSnapshot>(_terrainSelectionCellCount);
        foreach (TerrainCell source in data.EnumerateCells())
        {
            var target = new Vector3I(targetOrigin.X + source.X, targetOrigin.Y + source.Y, targetOrigin.Z + source.Z);
            before.Add(target, ReadTerrainCell(target));
            after.Add(target, new TerrainCellSnapshot(source.Shape, source.Style, source.Direction));
        }

        bool changed = false;
        foreach ((Vector3I cell, TerrainCellSnapshot value) in after)
        {
            if (before[cell] != value)
            {
                changed = true;
                break;
            }
        }
        if (!changed)
        {
            _status.Text = "The terrain copy already matches its destination.";
            return false;
        }

        ApplyTerrainSnapshots(after, createdTerrain: false, terrainShouldExist: true);
        RecordHistory("Paste terrain",
            () => ApplyTerrainSnapshots(before, createdTerrain: false, terrainShouldExist: true),
            () => ApplyTerrainSnapshots(after, createdTerrain: false, terrainShouldExist: true));
        _status.Text = $"Pasted {_terrainSelectionCellCount} terrain cells.";
        return true;
    }

    private void CancelTerrainCopy()
    {
        if (_terrainCopyPreview is not null)
        {
            _world.RemoveChild(_terrainCopyPreview);
            _terrainCopyPreview.QueueFree();
            _terrainCopyPreview = null;
        }
        _terrainCopyActive = false;
        RefreshTerrainSelectionStatus();
    }

    private void ClearTerrainSelection()
    {
        _terrainSelectionDragging = false;
        CancelTerrainCopy();
        if (_terrainSelectionPreview is not null)
        {
            _world.RemoveChild(_terrainSelectionPreview);
            _terrainSelectionPreview.QueueFree();
            _terrainSelectionPreview = null;
        }
        _terrainSelectionData = null;
        _terrainSelectionCellCount = 0;
        RefreshTerrainSelectionStatus();
    }

    private void RefreshTerrainSelectionStatus()
        => _terrainEditorWindow?.SetSelectionStatus(_terrainSelectionCellCount, _terrainCopyActive);
}
