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
        try
        {
            await WaitForUiFrames(2);
            if (_document is null || _welcome.Visible || !_editor.Visible)
                throw new InvalidOperationException("The actor hierarchy smoke requires an open project in the editor.");
            if (!_actorSearchField.IsVisibleInTree()
                || !_showActorCopiesToggle.IsVisibleInTree()
                || !_actorListSortButton.IsVisibleInTree())
                throw new InvalidOperationException("The actor hierarchy search, copy filter, or sort control is not visible.");
            if (!_actorStageToggleButton.IsVisibleInTree()
                || !_actorList.IsVisibleInTree()
                || !_offstageActorList.IsVisibleInTree())
                throw new InvalidOperationException("The stage action or one of the two actor hierarchy lists is not visible.");
            if (_actorList.Size.Y < 90f || _offstageActorList.Size.Y < 90f)
                throw new InvalidOperationException($"The stage lists have insufficient visible height: on={_actorList.Size.Y}px off={_offstageActorList.Size.Y}px.");

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
            WorldActorNode hierarchyTarget = CreateHierarchySmokeActor(
                source,
                $"hierarchy-smoke-target-{suffix}",
                $"UniqueHierarchyTarget-{suffix}",
                zetaRoot.LegacyName,
                string.Empty,
                origin + Vector3.Right + Vector3.Up * 2f);
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
            ActorHierarchyItemList selectedChildList = FindActorHierarchyList(child.LegacyName)
                ?? throw new InvalidOperationException("The child hierarchy row is missing.");
            selectedChildList.Select(selectedChildIndex, false);
            selectedChildList.EmitSignal(ItemList.SignalName.MultiSelected, (long)selectedChildIndex, true);
            if (_selectedActor != child)
                throw new InvalidOperationException("Selecting a hierarchy row did not select the actor identified by its metadata.");
            SetActorHierarchySmokeSearch("no hierarchy actor matches this");
            SetActorHierarchySmokeSearch(string.Empty);
            selectedChildIndex = FindActorHierarchyItem(child.LegacyName);
            selectedChildList = FindActorHierarchyList(child.LegacyName)
                ?? throw new InvalidOperationException("Refreshing or filtering the hierarchy hid a visible actor.");
            if (selectedChildIndex < 0 || !selectedChildList.GetSelectedItems().Contains(selectedChildIndex))
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
            int stageRootIndex = FindActorHierarchyItem(zetaRoot.LegacyName);
            if (stageRootIndex < 0 || FindActorHierarchyList(zetaRoot.LegacyName) != _actorList)
                throw new InvalidOperationException("The root actor is missing from the onstage list.");
            SelectActorHierarchyItemForSmoke(zetaRoot.LegacyName);
            if (_selectedActor != zetaRoot || _actorStageToggleButton.Disabled)
                throw new InvalidOperationException("The unparented stage root is not eligible for an on/offstage move.");
            zetaRoot.SetPlayMode(true);
            child.SetPlayMode(true);
            hierarchyTarget.SetPlayMode(true);
            if (zetaRoot.Freeze || child.Freeze || hierarchyTarget.Freeze)
                throw new InvalidOperationException("Onstage actors did not enter physics simulation.");
            _actorStageToggleButton.EmitSignal(BaseButton.SignalName.Pressed);
            if (!zetaRoot.State.IsOffstage
                || !child.IsOffstageEffective
                || !hierarchyTarget.IsOffstageEffective
                || FindActorHierarchyList(zetaRoot.LegacyName) != _offstageActorList
                || FindActorHierarchyList(child.LegacyName) != _offstageActorList
                || FindActorHierarchyList(hierarchyTarget.LegacyName) != _offstageActorList
                || FindActorHierarchyList(alphaRoot.LegacyName) != _actorList
                || FindActorHierarchyList(clone.LegacyName) != _actorList
                || !zetaRoot.Freeze
                || !child.Freeze
                || !hierarchyTarget.Freeze)
                throw new InvalidOperationException("Moving the hierarchy root offstage did not move its descendants between lists and out of physics.");

            SelectActorHierarchyItemForSmoke(child.LegacyName);
            if (!_actorStageToggleButton.Disabled)
                throw new InvalidOperationException("A parented actor could be toggled independently of its stage root.");
            int alphaSelectionIndex = _actorList.FindActorIndex(alphaRoot.LegacyName);
            _actorList.Select(alphaSelectionIndex);
            _actorList.EmitSignal(ItemList.SignalName.MultiSelected, (long)alphaSelectionIndex, true);
            if (_selectedActor != alphaRoot
                || _selectedActors.Count != 1
                || _offstageActorList.GetSelectedItems().Length != 0)
                throw new InvalidOperationException("Selecting an actor in the other stage list retained a stale selection.");

            UndoEdit();
            if (zetaRoot.State.IsOffstage
                || child.IsOffstageEffective
                || hierarchyTarget.IsOffstageEffective
                || FindActorHierarchyList(zetaRoot.LegacyName) != _actorList
                || FindActorHierarchyList(child.LegacyName) != _actorList
                || FindActorHierarchyList(hierarchyTarget.LegacyName) != _actorList
                || zetaRoot.Freeze
                || child.Freeze
                || hierarchyTarget.Freeze)
                throw new InvalidOperationException("Undo did not restore the onstage hierarchy, physics, and list placement.");
            RedoEdit();
            if (!zetaRoot.State.IsOffstage
                || !child.IsOffstageEffective
                || !hierarchyTarget.IsOffstageEffective
                || FindActorHierarchyList(zetaRoot.LegacyName) != _offstageActorList
                || FindActorHierarchyList(child.LegacyName) != _offstageActorList
                || FindActorHierarchyList(hierarchyTarget.LegacyName) != _offstageActorList
                || !zetaRoot.Freeze
                || !child.Freeze
                || !hierarchyTarget.Freeze)
                throw new InvalidOperationException("Redo did not reapply the offstage hierarchy, physics, and list placement.");
            UndoEdit();
            zetaRoot.SetPlayMode(false);
            child.SetPlayMode(false);
            hierarchyTarget.SetPlayMode(false);

            SetActorHierarchySmokeSearch(string.Empty);
            int childSourceIndex = FindActorHierarchyItem(child.LegacyName);
            int targetIndex = FindActorHierarchyItem(hierarchyTarget.LegacyName);
            if (childSourceIndex < 0 || targetIndex < 0)
                throw new InvalidOperationException("The actor-on-actor drop fixtures are missing from the onstage list.");
            _actorList.Select(childSourceIndex);
            _actorList.EnsureCurrentIsVisible();
            await WaitForUiFrames(1);
            targetIndex = FindActorHierarchyItem(hierarchyTarget.LegacyName);
            _actorList.Select(targetIndex);
            _actorList.EnsureCurrentIsVisible();
            await WaitForUiFrames(1);
            childSourceIndex = FindActorHierarchyItem(child.LegacyName);
            targetIndex = FindActorHierarchyItem(hierarchyTarget.LegacyName);
            var childMotionBeforeParentDrop = new ActorMotionSnapshot(child.GlobalTransform, child.LinearVelocity, child.AngularVelocity);
            Rect2 sourceRect = _actorList.GetItemRect(childSourceIndex);
            Rect2 targetRect = _actorList.GetItemRect(targetIndex);
            Vector2 sourcePosition = GetActorHierarchyItemCenter(_actorList, childSourceIndex);
            Vector2 targetPosition = GetActorHierarchyItemCenter(_actorList, targetIndex);
            Variant childDragData = _actorList._GetDragData(sourcePosition);
            if (!_actorList._CanDropData(targetPosition, childDragData))
                throw new InvalidOperationException(
                    $"The onstage list rejected a valid actor-on-actor parent drop: sourceIndex={childSourceIndex}, targetIndex={targetIndex}, sourceRect={sourceRect}, targetRect={targetRect}, listSize={_actorList.Size}, scroll={_actorList.GetVScrollBar().Value}, sourceHit={_actorList.GetItemAtPosition(sourcePosition, true)}, targetHit={_actorList.GetItemAtPosition(targetPosition, true)}, payloadType={childDragData.VariantType}, directCanDrop={CanDropHierarchyActor(child.LegacyName, hierarchyTarget.LegacyName, false)}.");
            _actorList._DropData(targetPosition, childDragData);
            string childSpawnParent = LegacySaveDocument.ReadString(child.State.Data["spawnTransformParent"]);
            if (child.State.TransformParent != hierarchyTarget.LegacyName
                || childSpawnParent != hierarchyTarget.LegacyName
                || child.GetParent() != hierarchyTarget
                || !child.GlobalTransform.IsEqualApprox(childMotionBeforeParentDrop.Transform)
                || !child.LinearVelocity.IsEqualApprox(childMotionBeforeParentDrop.Velocity)
                || !child.AngularVelocity.IsEqualApprox(childMotionBeforeParentDrop.AngularVelocity))
                throw new InvalidOperationException("Dropping an actor onto another actor did not set both parents while preserving world motion.");
            UndoEdit();
            if (child.State.TransformParent != zetaRoot.LegacyName
                || LegacySaveDocument.ReadString(child.State.Data["spawnTransformParent"]) != zetaRoot.LegacyName
                || child.GetParent() != zetaRoot
                || !child.GlobalTransform.IsEqualApprox(childMotionBeforeParentDrop.Transform))
                throw new InvalidOperationException("Undo did not restore the actor's previous hierarchy and world transform.");
            RedoEdit();
            if (child.State.TransformParent != hierarchyTarget.LegacyName || child.GetParent() != hierarchyTarget)
                throw new InvalidOperationException("Redo did not reapply the actor-on-actor parent drop.");

            childSourceIndex = FindActorHierarchyItem(child.LegacyName);
            int rootIndex = FindActorHierarchyItem(zetaRoot.LegacyName);
            _actorList.Select(childSourceIndex);
            _actorList.EnsureCurrentIsVisible();
            await WaitForUiFrames(1);
            Vector2 childTargetPosition = GetActorHierarchyItemCenter(_actorList, childSourceIndex);
            Variant selfDragData = _actorList._GetDragData(childTargetPosition);
            if (_actorList._CanDropData(childTargetPosition, selfDragData))
                throw new InvalidOperationException("The hierarchy accepted a self-parent drop.");
            _actorList.Select(rootIndex);
            _actorList.EnsureCurrentIsVisible();
            await WaitForUiFrames(1);
            childSourceIndex = FindActorHierarchyItem(child.LegacyName);
            rootIndex = FindActorHierarchyItem(zetaRoot.LegacyName);
            childTargetPosition = GetActorHierarchyItemCenter(_actorList, childSourceIndex);
            Vector2 rootPosition = GetActorHierarchyItemCenter(_actorList, rootIndex);
            Variant ancestorDragData = _actorList._GetDragData(rootPosition);
            if (_actorList._CanDropData(childTargetPosition, ancestorDragData))
                throw new InvalidOperationException("The hierarchy accepted a parent drop onto its descendant.");
            UndoEdit();


            SetActorHierarchySmokeSearch(child.State.DisplayName);
            childSourceIndex = FindActorHierarchyItem(child.LegacyName);
            if (childSourceIndex < 0 || _offstageActorList.ItemCount != 0)
                throw new InvalidOperationException("The stage-drop smoke could not isolate one onstage actor and an empty offstage target.");
            _actorList.Select(childSourceIndex);
            _actorList.EnsureCurrentIsVisible();
            await WaitForUiFrames(1);
            childSourceIndex = FindActorHierarchyItem(child.LegacyName);
            childDragData = _actorList._GetDragData(GetActorHierarchyItemCenter(_actorList, childSourceIndex));
            Vector2 emptyOffstagePosition = _offstageActorList.Size * 0.5f;
            if (_offstageActorList.GetItemAtPosition(emptyOffstagePosition, true) >= 0
                || !_offstageActorList._CanDropData(emptyOffstagePosition, childDragData))
                throw new InvalidOperationException("The offstage list rejected a valid drop onto empty space.");
            var childMotionBeforeStageDrop = new ActorMotionSnapshot(child.GlobalTransform, child.LinearVelocity, child.AngularVelocity);
            _offstageActorList._DropData(emptyOffstagePosition, childDragData);
            if (!child.State.IsOffstage
                || !child.IsOffstageEffective
                || child.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(child.State.Data["spawnTransformParent"]).Length != 0
                || child.GetParent() != _world
                || FindActorHierarchyList(child.LegacyName) != _offstageActorList
                || !child.GlobalTransform.IsEqualApprox(childMotionBeforeStageDrop.Transform)
                || !child.LinearVelocity.IsEqualApprox(childMotionBeforeStageDrop.Velocity)
                || !child.AngularVelocity.IsEqualApprox(childMotionBeforeStageDrop.AngularVelocity))
                throw new InvalidOperationException("Dropping an actor into the offstage list did not unparent it, move it offstage, and preserve world motion.");
            UndoEdit();
            if (child.State.IsOffstage
                || child.State.TransformParent != zetaRoot.LegacyName
                || LegacySaveDocument.ReadString(child.State.Data["spawnTransformParent"]) != zetaRoot.LegacyName
                || FindActorHierarchyList(child.LegacyName) != _actorList)
                throw new InvalidOperationException("Undo did not restore the actor's prior stage and transform/reset parents.");
            RedoEdit();
            if (!child.State.IsOffstage || child.State.TransformParent.Length != 0
                || FindActorHierarchyList(child.LegacyName) != _offstageActorList)
                throw new InvalidOperationException("Redo did not reapply the offstage empty-list drop.");
            UndoEdit();


            string screenshot = ProjectSettings.GlobalizePath("res://.godot/actor-hierarchy-smoke.png");
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            if (GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the actor hierarchy screenshot.");
            GD.Print($"ACTOR_HIERARCHY_SMOKE_OK search=case-insensitive=true clones=toggle=true order=parent-first=true sort=name+distance=true selection=filtered-refresh=true stage=dual-lists+toggle+undo+redo=true drag=parent+stage+undo+redo=true rejected=self+descendant=true inherited=true screenshot={screenshot}");
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

    private ActorHierarchyItemList? FindActorHierarchyList(string actorName)
    {
        if (_actorList.FindActorIndex(actorName) >= 0)
            return _actorList;
        if (_offstageActorList.FindActorIndex(actorName) >= 0)
            return _offstageActorList;
        return null;
    }

    private int FindActorHierarchyItem(string actorName)
        => FindActorHierarchyList(actorName)?.FindActorIndex(actorName) ?? -1;
    private static Vector2 GetActorHierarchyItemCenter(ActorHierarchyItemList list, int index)
    {
        Vector2 center = list.GetItemRect(index).GetCenter();
        center.Y -= (float)list.GetVScrollBar().Value;
        return center;
    }

    private void SelectActorHierarchyItemForSmoke(string actorName)
    {
        ActorHierarchyItemList list = FindActorHierarchyList(actorName)
            ?? throw new InvalidOperationException($"The hierarchy row for '{actorName}' is missing.");
        int index = list.FindActorIndex(actorName);
        _actorList.DeselectAll();
        _offstageActorList.DeselectAll();
        list.Select(index, false);
        list.EmitSignal(ItemList.SignalName.MultiSelected, (long)index, true);
    }
    private void SetActorHierarchySmokeSearch(string text)
    {
        _actorSearchField.Text = text;
        _actorSearchField.EmitSignal(LineEdit.SignalName.TextChanged, text);
    }
}
