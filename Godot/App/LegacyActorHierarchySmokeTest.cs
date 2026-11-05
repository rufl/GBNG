using System.Text.Json.Nodes;
using Godot;
using GBNG.Domain;
using GBNG.World;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunActorHierarchyNavigationSmoke()
    {
        int exitCode = 0;
        bool originalShowCopies = _showActorCopies;
        bool originalSortByDistance = _sortActorListByDistance;
        ActorStageFilter originalStageFilter = _actorStageFilter;
        try
        {
            await WaitForUiFrames(2);
            if (_document is null || _welcome.Visible || !_editor.Visible)
                throw new InvalidOperationException("The actor hierarchy smoke requires an open project in the editor.");
            if (!_actorSearchField.IsVisibleInTree()
                || !_showActorCopiesToggle.IsVisibleInTree()
                || !_actorListSortButton.IsVisibleInTree())
                throw new InvalidOperationException("The actor hierarchy search, copy filter, or sort control is not visible.");
            if (!_actorStageFilterPicker.IsVisibleInTree() || !_actorStageToggleButton.IsVisibleInTree())
                throw new InvalidOperationException("The on/offstage filter or stage toggle is not visible.");
            if (_actorList.Size.Y < 120f)
                throw new InvalidOperationException($"The actor hierarchy has insufficient visible height: {_actorList.Size.Y}px.");

            string suffix = Guid.NewGuid().ToString("N");
            WorldActorNode source = _actors.FirstOrDefault(actor =>
                    !actor.State.WasClonedByScript
                    && !actor.LegacyName.StartsWith("builtin:", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The smoke project has no actor suitable for hierarchy fixtures.");
            Vector3 origin = WorldActorNode.FindPlayerActorAtSlot(_actors, 1)?.GlobalPosition ?? _cameraTarget;
            WorldActorNode zetaRoot = CreateHierarchySmokeActor(
                source,
                $"hierarchy-smoke-zeta-{suffix}",
                $"Zeta Hierarchy Root {suffix}",
                string.Empty,
                string.Empty,
                origin + Vector3.Right);
            WorldActorNode child = CreateHierarchySmokeActor(
                source,
                $"hierarchy-smoke-child-{suffix}",
                $"UniqueHierarchyChild-{suffix}",
                zetaRoot.LegacyName,
                string.Empty,
                origin + Vector3.Right + Vector3.Up);
            WorldActorNode clone = CreateHierarchySmokeActor(
                source,
                $"hierarchy-smoke-copy-{suffix}",
                $"Hierarchy Copy {suffix}",
                string.Empty,
                zetaRoot.LegacyName,
                origin + Vector3.Right * 2f);
            WorldActorNode alphaRoot = CreateHierarchySmokeActor(
                source,
                $"hierarchy-smoke-alpha-{suffix}",
                $"Alpha Hierarchy Root {suffix}",
                string.Empty,
                string.Empty,
                origin + Vector3.Right * 100f);

            SetActorListSortMode(sortByDistance: false, persist: false);
            _showActorCopiesToggle.SetPressedNoSignal(true);
            SetShowActorCopiesPreference(true);
            SetActorHierarchySmokeSearch(string.Empty);
            RefreshActorList();
            int alphaIndex = FindActorHierarchyItem(alphaRoot.LegacyName);
            int zetaIndex = FindActorHierarchyItem(zetaRoot.LegacyName);
            int childIndex = FindActorHierarchyItem(child.LegacyName);
            if (alphaIndex < 0 || zetaIndex <= alphaIndex || childIndex <= zetaIndex
                || !_actorList.GetItemText(childIndex).Contains("↳", StringComparison.Ordinal))
                throw new InvalidOperationException("Name sorting did not preserve alphabetic sibling order and parent-first indentation.");
            if (FindActorHierarchyItem(clone.LegacyName) < 0)
                throw new InvalidOperationException("The hierarchy did not show copies when the Show copies option was enabled.");

            SetActorHierarchySmokeSearch(child.State.DisplayName.ToLowerInvariant());
            if (_actorList.ItemCount != 1
                || _actorList.GetItemMetadata(0).AsString() != child.LegacyName
                || !_actorList.GetItemText(0).Contains("↳", StringComparison.Ordinal))
                throw new InvalidOperationException("Case-insensitive search did not return the matching indented child actor.");

            SetActorHierarchySmokeSearch(string.Empty);
            _showActorCopiesToggle.SetPressedNoSignal(false);
            _showActorCopiesToggle.EmitSignal(BaseButton.SignalName.Toggled, false);
            if (FindActorHierarchyItem(clone.LegacyName) >= 0
                || FindActorHierarchyItem(zetaRoot.LegacyName) < 0)
                throw new InvalidOperationException("Hiding copies removed a normal actor or left a user-created copy visible.");
            _showActorCopiesToggle.SetPressedNoSignal(true);
            _showActorCopiesToggle.EmitSignal(BaseButton.SignalName.Toggled, true);

            int selectedChildIndex = FindActorHierarchyItem(child.LegacyName);
            _actorList.Select(selectedChildIndex, false);
            _actorList.EmitSignal(ItemList.SignalName.MultiSelected, (long)selectedChildIndex, true);
            if (_selectedActor != child)
                throw new InvalidOperationException("Selecting a hierarchy row did not select the actor identified by its metadata.");
            SetActorHierarchySmokeSearch("no hierarchy actor matches this");
            SetActorHierarchySmokeSearch(string.Empty);
            selectedChildIndex = FindActorHierarchyItem(child.LegacyName);
            if (selectedChildIndex < 0 || !_actorList.GetSelectedItems().Contains(selectedChildIndex))
                throw new InvalidOperationException("Refreshing or filtering the hierarchy lost selection for a visible actor.");

            alphaRoot.GlobalPosition = origin + Vector3.Right * 100f;
            zetaRoot.GlobalPosition = origin + Vector3.Right * 1f;
            SetActorListSortMode(sortByDistance: false, persist: false);
            _actorListSortButton.EmitSignal(BaseButton.SignalName.Pressed);
            if (!_sortActorListByDistance
                || FindActorHierarchyItem(zetaRoot.LegacyName) >= FindActorHierarchyItem(alphaRoot.LegacyName))
                throw new InvalidOperationException("Distance sorting did not prioritize the nearest hierarchy branch.");
            _actorListSortButton.EmitSignal(BaseButton.SignalName.Pressed);
            if (_sortActorListByDistance
                || FindActorHierarchyItem(alphaRoot.LegacyName) >= FindActorHierarchyItem(zetaRoot.LegacyName))
                throw new InvalidOperationException("The sort control did not switch back to alphabetical hierarchy order.");
            SetActorStageFilter((long)ActorStageFilter.OnStage);
            int stageRootIndex = FindActorHierarchyItem(zetaRoot.LegacyName);
            if (stageRootIndex < 0)
                throw new InvalidOperationException("The root actor is missing from the onstage view.");
            _actorList.DeselectAll();
            _actorList.Select(stageRootIndex, false);
            _actorList.EmitSignal(ItemList.SignalName.MultiSelected, (long)stageRootIndex, true);
            if (_selectedActor != zetaRoot || _actorStageToggleButton.Disabled)
                throw new InvalidOperationException("The unparented stage root is not eligible for an on/offstage move.");
            zetaRoot.SetPlayMode(true);
            child.SetPlayMode(true);
            if (zetaRoot.Freeze || child.Freeze)
                throw new InvalidOperationException("Onstage actors did not enter physics simulation.");
            _actorStageToggleButton.EmitSignal(BaseButton.SignalName.Pressed);
            if (!zetaRoot.State.IsOffstage
                || !child.IsOffstageEffective
                || _actorStageFilter != ActorStageFilter.OffStage
                || FindActorHierarchyItem(zetaRoot.LegacyName) < 0
                || FindActorHierarchyItem(child.LegacyName) < 0
                || FindActorHierarchyItem(alphaRoot.LegacyName) >= 0
                || FindActorHierarchyItem(clone.LegacyName) >= 0
                || !zetaRoot.Freeze
                || !child.Freeze)
                throw new InvalidOperationException("Moving the hierarchy root offstage did not move its descendants out of physics and into the offstage view.");

            int offstageChildIndex = FindActorHierarchyItem(child.LegacyName);
            _actorList.DeselectAll();
            _actorList.Select(offstageChildIndex, false);
            _actorList.EmitSignal(ItemList.SignalName.MultiSelected, (long)offstageChildIndex, true);
            if (!_actorStageToggleButton.Disabled)
                throw new InvalidOperationException("A parented actor could be toggled independently of its stage root.");

            _actorStageFilterPicker.Select((int)ActorStageFilter.OnStage);
            _actorStageFilterPicker.EmitSignal(OptionButton.SignalName.ItemSelected, (long)ActorStageFilter.OnStage);
            if (FindActorHierarchyItem(zetaRoot.LegacyName) >= 0
                || FindActorHierarchyItem(child.LegacyName) >= 0
                || FindActorHierarchyItem(alphaRoot.LegacyName) < 0)
                throw new InvalidOperationException("The onstage filter did not exclude offstage hierarchies.");

            UndoEdit();
            if (zetaRoot.State.IsOffstage
                || child.IsOffstageEffective
                || _actorStageFilter != ActorStageFilter.OnStage
                || FindActorHierarchyItem(zetaRoot.LegacyName) < 0
                || FindActorHierarchyItem(child.LegacyName) < 0
                || zetaRoot.Freeze
                || child.Freeze)
                throw new InvalidOperationException("Undo did not restore the original stage state, physics, and hierarchy view.");
            RedoEdit();
            if (!zetaRoot.State.IsOffstage
                || !child.IsOffstageEffective
                || _actorStageFilter != ActorStageFilter.OffStage
                || FindActorHierarchyItem(zetaRoot.LegacyName) < 0
                || FindActorHierarchyItem(child.LegacyName) < 0
                || !zetaRoot.Freeze
                || !child.Freeze)
                throw new InvalidOperationException("Redo did not reapply the offstage hierarchy and physics state.");
            UndoEdit();
            zetaRoot.SetPlayMode(false);
            child.SetPlayMode(false);

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/actor-hierarchy-smoke.png");
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            if (GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the actor hierarchy screenshot.");
            GD.Print($"ACTOR_HIERARCHY_SMOKE_OK search=case-insensitive=true clones=toggle=true order=parent-first=true sort=name+distance=true selection=filtered-refresh=true stage=filter+toggle+undo+redo=true inherited=true screenshot={screenshot}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ACTOR_HIERARCHY_SMOKE_FAILED: {exception}");
        }
        finally
        {
            SetActorHierarchySmokeSearch(string.Empty);
            _showActorCopies = originalShowCopies;
            _showActorCopiesToggle.SetPressedNoSignal(originalShowCopies);
            SetActorListSortMode(originalSortByDistance, persist: false);
            SaveEditorPreferences();
            SetActorStageFilter((long)originalStageFilter);
            GetTree().Quit(exitCode);
        }
    }

    private WorldActorNode CreateHierarchySmokeActor(
        WorldActorNode source,
        string name,
        string displayName,
        string transformParent,
        string cloneParent,
        Vector3 position)
    {
        JsonObject data = source.State.Data.DeepClone().AsObject();
        data["name"] = name;
        data["displayName"] = displayName;
        data["transformParent"] = transformParent;
        data["spawnTransformParent"] = transformParent;
        data["cloneParent"] = cloneParent;
        data["wasClonedByScript"] = false;
        data["preferOffstage"] = false;
        data["enablePhysics"] = true;
        data["enableGravity"] = false;
        LegacyActorData state = _document!.InsertActor(data, _document.Actors.Count);
        state.SetWorldTransform(new Transform3D(source.GlobalTransform.Basis, position));
        return CreateActorNode(state, _actors.Count);
    }

    private int FindActorHierarchyItem(string actorName)
    {
        for (int index = 0; index < _actorList.ItemCount; index++)
        {
            if (_actorList.GetItemMetadata(index).AsString() == actorName)
                return index;
        }
        return -1;
    }
    private void SetActorHierarchySmokeSearch(string text)
    {
        _actorSearchField.Text = text;
        _actorSearchField.EmitSignal(LineEdit.SignalName.TextChanged, text);
    }
}
