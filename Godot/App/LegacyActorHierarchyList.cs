using Godot;
using GBNG.Domain;
using GBNG.World;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private readonly record struct ActorHierarchyRow(WorldActorNode Actor, WorldActorNode[] Path, bool IsCopy);

    private LineEdit _actorSearchField = null!;
    private CheckBox _showActorCopiesToggle = null!;
    private Button _actorListSortButton = null!;
    private bool _showActorCopies = true;
    private bool _sortActorListByDistance;
    private double _actorListSortRefreshSeconds;

    private void PopulateHierarchyActorList()
    {
        _syncingActorList = true;
        try
        {
            _actorList.Clear();
            string search = _actorSearchField.Text.Trim();
            var actorsByName = _actors.ToDictionary(actor => actor.LegacyName, StringComparer.Ordinal);
            var rows = new List<ActorHierarchyRow>(_actors.Count);
            foreach (WorldActorNode actor in _actors)
            {
                WorldActorNode[] path = BuildActorHierarchyPath(actor, actorsByName);
                if (actor.State.WasClonedByScript)
                    continue;
                string cloneParent = LegacySaveDocument.ReadString(actor.State.Data["cloneParent"]);
                bool isCopy = cloneParent.Length > 0;
                if (isCopy && !_showActorCopies)
                    continue;
                string displayName = GetActorHierarchyDisplayName(actor);
                if (search.Length > 0 && !displayName.Contains(search, StringComparison.OrdinalIgnoreCase))
                    continue;
                rows.Add(new ActorHierarchyRow(actor, path, isCopy));
            }

            WorldActorNode? localPlayer = WorldActorNode.FindPlayerActorAtSlot(_actors, 1);
            Vector3 sortOrigin = localPlayer?.GlobalPosition ?? _cameraTarget;
            rows.Sort((left, right) => CompareActorHierarchyRows(left, right, sortOrigin));
            foreach (ActorHierarchyRow row in rows)
            {
                string indentation = new(' ', Math.Max(0, row.Path.Length - 1) * 2);
                string branchMarker = row.Path.Length > 1 ? "↳ " : string.Empty;
                string copyMarker = row.IsCopy ? "(copy) " : string.Empty;
                _actorList.AddItem($"{indentation}{branchMarker}{copyMarker}{GetActorHierarchyDisplayName(row.Actor)}");
                _actorList.SetItemMetadata(_actorList.ItemCount - 1, row.Actor.LegacyName);
            }
        }
        finally
        {
            _syncingActorList = false;
        }
    }

    private static WorldActorNode[] BuildActorHierarchyPath(
        WorldActorNode actor,
        IReadOnlyDictionary<string, WorldActorNode> actorsByName)
    {
        var path = new List<WorldActorNode>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        WorldActorNode? current = actor;
        while (current is not null && visited.Add(current.LegacyName))
        {
            path.Add(current);
            current = actorsByName.TryGetValue(current.State.TransformParent, out WorldActorNode? parent)
                ? parent
                : null;
        }
        path.Reverse();
        return path.ToArray();
    }

    private int CompareActorHierarchyRows(ActorHierarchyRow left, ActorHierarchyRow right, Vector3 sortOrigin)
    {
        int common = 0;
        int sharedLength = Math.Min(left.Path.Length, right.Path.Length);
        while (common < sharedLength && left.Path[common].LegacyName == right.Path[common].LegacyName)
            common++;
        if (common == sharedLength)
            return left.Path.Length.CompareTo(right.Path.Length);

        WorldActorNode leftBranch = left.Path[common];
        WorldActorNode rightBranch = right.Path[common];
        if (_sortActorListByDistance)
        {
            int distanceOrder = leftBranch.GlobalPosition.DistanceSquaredTo(sortOrigin)
                .CompareTo(rightBranch.GlobalPosition.DistanceSquaredTo(sortOrigin));
            if (distanceOrder != 0)
                return distanceOrder;
        }

        int nameOrder = StringComparer.OrdinalIgnoreCase.Compare(
            GetActorHierarchyDisplayName(leftBranch),
            GetActorHierarchyDisplayName(rightBranch));
        return nameOrder != 0
            ? nameOrder
            : StringComparer.Ordinal.Compare(leftBranch.LegacyName, rightBranch.LegacyName);
    }

    private static string GetActorHierarchyDisplayName(WorldActorNode actor)
        => actor.State.DisplayName.Length > 0 ? actor.State.DisplayName : actor.LegacyName;

    private void ToggleActorListSort()
        => SetActorListSortMode(!_sortActorListByDistance, persist: true);

    private void SetActorListSortMode(bool sortByDistance, bool persist)
    {
        _sortActorListByDistance = sortByDistance;
        _actorListSortRefreshSeconds = 0d;
        _actorListSortButton.Text = sortByDistance ? "Sort: distance" : "Sort: name";
        _actorListSortButton.TooltipText = sortByDistance
            ? "Sort siblings by distance from the local player, or the editor camera when no player exists."
            : "Sort siblings alphabetically while keeping parents above their children.";
        if (persist)
            SaveEditorPreferences();
        RefreshActorList();
    }

    private void SetShowActorCopiesPreference(bool showCopies)
    {
        _showActorCopies = showCopies;
        SaveEditorPreferences();
        RefreshActorList();
    }

    private void UpdateActorHierarchySort(double delta)
    {
        if (!_sortActorListByDistance || _playMode || !_actorListVisible || _document is null)
        {
            _actorListSortRefreshSeconds = 0d;
            return;
        }
        _actorListSortRefreshSeconds += delta;
        if (_actorListSortRefreshSeconds < 2d)
            return;
        _actorListSortRefreshSeconds = 0d;
        RefreshActorList();
    }
}
