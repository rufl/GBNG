using Godot;
using GBNG.Domain;
using GBNG.World;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private readonly record struct ActorHierarchyRow(WorldActorNode Actor, WorldActorNode[] Path, bool IsCopy, bool IsOffstage);
    private sealed partial class ActorHierarchyItemList : ItemList
    {
        private Func<string, string?, bool>? _canDropActor;
        private Action<string, string?>? _dropActor;

        public void Configure(
            bool isOffstage,
            Func<string, string?, bool> canDropActor,
            Action<string, string?> dropActor)
        {
            _canDropActor = canDropActor;
            _dropActor = dropActor;
            TooltipText = isOffstage
                ? "Drop an actor on blank space to move it offstage; drop on a row to parent it."
                : "Drop an actor on blank space to move it on stage; drop on a row to parent it.";
        }

        public override Variant _GetDragData(Vector2 atPosition)
        {
            int index = GetItemAtPosition(atPosition, true);
            if (index < 0)
                return default;

            Variant payload = GetItemMetadata(index);
            if (payload.VariantType != Variant.Type.String)
                return default;
            if (GetViewport().GuiIsDragging())
                SetDragPreview(new Label { Text = GetItemText(index) });
            return payload;
        }

        public override bool _CanDropData(Vector2 atPosition, Variant data)
        {
            if (data.VariantType != Variant.Type.String)
                return false;
            return _canDropActor?.Invoke(data.AsString(), GetActorNameAt(atPosition)) ?? false;
        }

        public override void _DropData(Vector2 atPosition, Variant data)
        {
            if (_CanDropData(atPosition, data))
                _dropActor?.Invoke(data.AsString(), GetActorNameAt(atPosition));
        }

        public int FindActorIndex(string actorName)
        {
            for (int index = 0; index < ItemCount; index++)
            {
                if (GetItemMetadata(index).AsString() == actorName)
                    return index;
            }
            return -1;
        }

        private string? GetActorNameAt(Vector2 position)
        {
            int index = GetItemAtPosition(position, true);
            if (index < 0)
                return null;
            Variant metadata = GetItemMetadata(index);
            return metadata.VariantType == Variant.Type.String ? metadata.AsString() : null;
        }
    }


    private LineEdit _actorSearchField = null!;
    private CheckBox _showActorCopiesToggle = null!;
    private Button _actorListSortButton = null!;
    private bool _showActorCopies = true;
    private bool _sortActorListByDistance;
    private double _actorListSortRefreshSeconds;
    private Button _actorStageToggleButton = null!;

    private Control BuildActorStageControls()
    {
        var controls = new HBoxContainer { Name = "ActorStageControls" };
        _actorStageToggleButton = new Button
        {
            Name = "ActorStageToggleButton",
            Text = "Move offstage",
            TooltipText = "Move the selected unparented actor off stage.",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, 28f)
        };
        _actorStageToggleButton.Pressed += ToggleSelectedActorOffstage;
        controls.AddChild(_actorStageToggleButton);
        UpdateActorStageToggleButton();
        return controls;
    }

    private void PopulateHierarchyActorList()
    {
        _syncingActorList = true;
        try
        {
            _actorList.Clear();
            _offstageActorList.Clear();
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
                rows.Add(new ActorHierarchyRow(actor, path, isCopy, actor.IsOffstageEffective));
            }

            WorldActorNode? localPlayer = WorldActorNode.FindPlayerActorAtSlot(
                _actors, 1, _lanSession.Players, _lanSession.HasPeer, _document);
            Vector3 sortOrigin = localPlayer?.GlobalPosition ?? _cameraTarget;
            rows.Sort((left, right) => CompareActorHierarchyRows(left, right, sortOrigin));
            foreach (ActorHierarchyRow row in rows)
            {
                string indentation = new(' ', Math.Max(0, row.Path.Length - 1) * 2);
                string branchMarker = row.Path.Length > 1 ? "↳ " : string.Empty;
                string copyMarker = row.IsCopy ? "(copy) " : string.Empty;
                ActorHierarchyItemList list = row.IsOffstage ? _offstageActorList : _actorList;
                list.AddItem($"{indentation}{branchMarker}{copyMarker}{GetActorHierarchyDisplayName(row.Actor)}");
                list.SetItemMetadata(list.ItemCount - 1, row.Actor.LegacyName);
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

    private void UpdateActorStageToggleButton()
    {
        if (_actorStageToggleButton is null)
            return;
        bool hasSingleSelection = _selectedActors.Count == 1 && _selectedActor is not null;
        bool isParented = hasSingleSelection && _selectedActor!.State.TransformParent.Length > 0;
        _actorStageToggleButton.Text = !hasSingleSelection
            ? "Move offstage"
            : isParented
                ? "Inherits stage"
                : _selectedActor!.State.IsOffstage ? "Bring on stage" : "Move offstage";
        _actorStageToggleButton.Disabled = _document is null || _playMode || !hasSingleSelection || isParented;
        _actorStageToggleButton.TooltipText = isParented
            ? "A parented actor inherits its stage from its hierarchy root."
            : _selectedActor?.State.IsOffstage == true
                ? "Bring the selected actor and its descendants on stage."
                : "Move the selected unparented actor and its descendants off stage.";
    }


    private void ToggleSelectedActorOffstage()
    {
        if (_document is null || _playMode || _selectedActors.Count != 1 || _selectedActor is not WorldActorNode actor)
            return;
        if (actor.State.TransformParent.Length > 0)
        {
            _status.Text = "A parented actor inherits its stage from the hierarchy root.";
            return;
        }

        string actorName = actor.LegacyName;
        string displayName = GetActorHierarchyDisplayName(actor);
        bool oldOffstage = actor.State.IsOffstage;
        bool newOffstage = !oldOffstage;
        ApplyActorOffstagePreference(actorName, newOffstage);
        RecordHistory(
            newOffstage ? $"Move {displayName} offstage" : $"Bring {displayName} on stage",
            () => ApplyActorOffstagePreference(actorName, oldOffstage),
            () => ApplyActorOffstagePreference(actorName, newOffstage));
        _status.Text = newOffstage ? $"Moved {displayName} offstage." : $"Brought {displayName} on stage.";
    }

    private void ApplyActorOffstagePreference(string actorName, bool offstage)
    {
        WorldActorNode? actor = FindActor(actorName);
        if (actor is null || actor.State.IsOffstage == offstage)
            return;

        actor.State.WriteBool("preferOffstage", offstage);
        foreach (WorldActorNode candidate in _actors)
        {
            if (candidate == actor || actor.IsAncestorOf(candidate))
                candidate.ApplyLegacyPhysicsSettings();
        }
        RefreshActorList();
    }


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
