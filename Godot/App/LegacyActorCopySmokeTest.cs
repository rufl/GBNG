using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using GBNG.Domain;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunActorCopySmoke()
    {
        int exitCode = 0;
        string? originalProjectPath = _document?.SourcePath;
        string? temporaryDirectory = null;
        try
        {
            await WaitForUiFrames(2);
            if (_document is null || originalProjectPath is null)
                throw new InvalidOperationException("Open a project before running the actor-copy smoke test.");

            string sourcePath = ProjectSettings.GlobalizePath(originalProjectPath);
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("The actor-copy smoke project was not found.", sourcePath);
            temporaryDirectory = Path.Combine(Path.GetTempPath(), $"GBNG-ActorCopy-Smoke-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            string projectPath = Path.Combine(temporaryDirectory, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, projectPath);
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            if (_document?.SourcePath != projectPath)
                throw new InvalidOperationException("The writable actor-copy smoke project did not load.");
            if (_assetStoreWindow.Visible || _worldSettingsWindow.Visible
                || ((Window)_worldSettingsWindow.FindChild("WorldMapSizeDialog", true, false)).Visible)
                throw new InvalidOperationException("Editor utility windows must remain hidden until opened by the user.");

            WorldActorNode source = _actors
                .Where(actor => !actor.LegacyName.StartsWith("builtin:", StringComparison.Ordinal)
                    && LegacySaveDocument.ReadString(actor.State.Data["cloneParent"]).Length == 0
                    && actor.GetNodeOrNull<Node3D>("Renderable") is not null)
                .OrderByDescending(actor => actor.GetWorldRenderBounds().Size.LengthSquared())
                .FirstOrDefault()
                ?? throw new InvalidOperationException("The smoke project has no renderable original actor to copy.");
            string sourceName = source.LegacyName;
            JsonObject childData = (JsonObject)source.State.Data.DeepClone();
            string childName = $"copy-smoke-child-{Guid.NewGuid():N}";
            childData["name"] = childName;
            childData["displayName"] = "Copy smoke child";
            childData["transformParent"] = source.LegacyName;
            childData["cloneParent"] = "";
            int childIndex = _document.Actors.Count;
            LegacyActorData childState = _document.InsertActor(childData, childIndex);
            WorldActorNode child = CreateActorNode(childState, childIndex);
            child.GlobalPosition = source.GlobalPosition + new Vector3(0f, 1.5f, 0f);
            child.SaveTransform();
            ClearHistory();
            SelectActor(source);
            await WaitForUiFrames(2);
            _cameraTarget = source.GlobalPosition;
            _cameraDistance = 12f;
            UpdateOrbitCamera();
            await WaitForUiFrames(2);

            int originalCount = _document.Actors.Count;
            var originalActorNames = _actors.Select(actor => actor.LegacyName).ToHashSet(StringComparer.Ordinal);
            int existingCopies = _actors.Count(actor => actor.LegacyName != sourceName
                && LegacySaveDocument.ReadString(actor.State.Data["cloneParent"]) == sourceName);
            Button copyButton = FindChild("CopyActorsButton", true, false) as Button
                ?? throw new InvalidOperationException("The actor-copy toolbar button was not found.");
            _UnhandledInput(new InputEventKey { Keycode = Key.C, CtrlPressed = true, Pressed = true });
            await WaitForUiFrames(1);
            if (!_copyPlacementActive)
                throw new InvalidOperationException("Ctrl+C did not activate the actor-copy placement tool.");
            CancelCopyPlacement(showStatus: false);
            await WaitForUiFrames(1);
            copyButton.EmitSignal("pressed");
            await WaitForUiFrames(2);
            if (!_copyPlacementActive || _copyPreviewRoot is null || _copyPreviewRoot.GetChildCount() != 2
                || _document.Actors.Count != originalCount)
                throw new InvalidOperationException("Copy mode did not show the actor and descendant previews without modifying the project.");
            GeometryInstance3D? previewGeometry = _copyPreviewRoot
                .FindChildren("*", "GeometryInstance3D", true, false)
                .OfType<GeometryInstance3D>()
                .FirstOrDefault();
            if (previewGeometry is null || previewGeometry.MaterialOverride is null)
                throw new InvalidOperationException("The actor-copy preview has no rendered geometry or translucent material override.");
            UpdateCopyPreview(new Vector2(680f, 360f), snapToGrid: false);
            Vector2 cursorProjection = _camera.UnprojectPosition(_copyPreviewRoot.GlobalPosition);
            if (cursorProjection.DistanceTo(new Vector2(680f, 360f)) > 1f)
                throw new InvalidOperationException("Copy placement did not follow the cursor ray.");
            UpdateCopyPreview(new Vector2(680f, 360f), snapToGrid: true);
            Vector3 snappedPosition = _copyPreviewRoot.GlobalPosition;
            if (!Mathf.IsEqualApprox(snappedPosition.X / CopySnapHorizontal, Mathf.Round(snappedPosition.X / CopySnapHorizontal))
                || !Mathf.IsEqualApprox(snappedPosition.Y / CopySnapVertical, Mathf.Round(snappedPosition.Y / CopySnapVertical))
                || !Mathf.IsEqualApprox(snappedPosition.Z / CopySnapHorizontal, Mathf.Round(snappedPosition.Z / CopySnapHorizontal)))
                throw new InvalidOperationException("Ctrl/Cmd grid snapping did not align the preview to the legacy placement grid.");
            _copyPreviewRoot.GlobalPosition = source.GlobalPosition + Vector3.Right * 3f;
            await WaitForUiFrames(3);
            await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
            Vector2 previewScreen = _camera.UnprojectPosition(previewGeometry.GlobalPosition);
            Image previewCapture = GetViewport().GetTexture().GetImage();
            int cyanPreviewPixels = 0;
            int minX = Mathf.Max(0, Mathf.RoundToInt(previewScreen.X) - 32);
            int maxX = Mathf.Min(previewCapture.GetWidth() - 1, Mathf.RoundToInt(previewScreen.X) + 32);
            int minY = Mathf.Max(0, Mathf.RoundToInt(previewScreen.Y) - 32);
            int maxY = Mathf.Min(previewCapture.GetHeight() - 1, Mathf.RoundToInt(previewScreen.Y) + 32);
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    Color pixel = previewCapture.GetPixel(x, y);
                    if (pixel.R < 0.4f && pixel.G > 0.5f && pixel.B > 0.55f)
                        cyanPreviewPixels++;
                }
            }
            if (cyanPreviewPixels == 0)
                throw new InvalidOperationException("The copy preview was not visible in the rendered editor view.");
            string screenshot = ProjectSettings.GlobalizePath("res://.godot/actor-copy-preview-smoke.png");
            if (previewCapture.SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("The actor-copy preview screenshot could not be saved.");

            _UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            await WaitForUiFrames(1);
            if (_copyPlacementActive || _document.Actors.Count != originalCount)
                throw new InvalidOperationException("Canceling copy placement changed the project.");

            copyButton.EmitSignal("pressed");
            await WaitForUiFrames(1);
            Vector3 placement = source.GlobalPosition + new Vector3(3f, 0f, 0f);
            PasteCopyAt(placement);
            await WaitForUiFrames(2);
            List<WorldActorNode> firstCopies = FindCopiesSince(originalActorNames);
            if (firstCopies.Count != 2 || _document.Actors.Count != originalCount + 2)
                throw new InvalidOperationException("Copy placement did not duplicate the selected actor and its child.");

            WorldActorNode firstRootCopy = firstCopies.Single(actor =>
                LegacySaveDocument.ReadString(actor.State.Data["cloneParent"]) == source.LegacyName);
            WorldActorNode firstChildCopy = firstCopies.Single(actor => actor.State.TransformParent == firstRootCopy.LegacyName);
            if (firstRootCopy.State.DisplayName != GetCopyDisplayNameForSmoke(sourceName, existingCopies + 1))
                throw new InvalidOperationException("The new actor copy did not receive the next legacy clone display name.");
            AssertCopyTransform(source, firstRootCopy, placement - source.GlobalPosition);
            AssertCopyTransform(child, firstChildCopy, placement - source.GlobalPosition);
            if (firstRootCopy.State.RenderableUri != source.State.RenderableUri
                || firstRootCopy.State.BrainName != source.State.BrainName
                || !JsonNode.DeepEquals(firstRootCopy.State.Data["memoryJson"], source.State.Data["memoryJson"]))
                throw new InvalidOperationException("The copied actor did not preserve its legacy behavior and renderable data.");

            PasteCopyAt(placement + new Vector3(2f, 0f, 0f));
            await WaitForUiFrames(2);
            if (_document.Actors.Count != originalCount + 4)
                throw new InvalidOperationException("Copy mode did not remain active for repeated placements.");
            WorldActorNode secondRootCopy = _actors.Single(actor =>
                !originalActorNames.Contains(actor.LegacyName)
                && !firstCopies.Contains(actor)
                && LegacySaveDocument.ReadString(actor.State.Data["cloneParent"]) == sourceName);
            string secondRootCopyName = secondRootCopy.LegacyName;
            if (secondRootCopy.State.DisplayName != GetCopyDisplayNameForSmoke(sourceName, existingCopies + 2))
                throw new InvalidOperationException("Repeated copies did not receive the next legacy clone display name.");

            UndoEdit();
            await WaitForUiFrames(2);
            if (_document.Actors.Count != originalCount + 2 || _actors.Any(actor => actor.LegacyName == secondRootCopyName))
                throw new InvalidOperationException("Undo did not remove the latest actor-copy group.");
            RedoEdit();
            await WaitForUiFrames(2);
            if (_document.Actors.Count != originalCount + 4
                || !_actors.Any(actor => actor.LegacyName == secondRootCopyName))
                throw new InvalidOperationException("Redo did not restore the latest actor-copy group.");

            _UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            _document.SaveTo(projectPath);
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            if (_document?.SourcePath != projectPath || _document.Actors.Count != originalCount + 4)
                throw new InvalidOperationException("Copied actor hierarchies did not persist after saving and reloading the project.");
            if (!_actors.Any(actor => LegacySaveDocument.ReadString(actor.State.Data["cloneParent"]) == sourceName))
                throw new InvalidOperationException("Saved copy lineage was not retained in the .voos project.");

            GD.Print($"ACTOR_COPY_SMOKE_OK preview=true ctrlC=true snap=true descendants=true repeat=true undo=redo=persistence=true screenshot={screenshot}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ACTOR_COPY_SMOKE_FAILED: {exception}");
        }
        finally
        {
            CancelCopyPlacement(showStatus: false);
            if (originalProjectPath is not null)
            {
                LoadProject(originalProjectPath);
                await WaitForUiFrames(2);
            }
            if (temporaryDirectory is not null && Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }

        GetTree().Quit(exitCode);
    }

    private List<WorldActorNode> FindCopiesSince(HashSet<string> originalActorNames)
    {
        return _actors.Where(actor => !originalActorNames.Contains(actor.LegacyName)).ToList();
    }

    private static void AssertCopyTransform(WorldActorNode source, WorldActorNode copy, Vector3 expectedOffset)
    {
        if (copy.GlobalPosition.DistanceTo(source.GlobalPosition + expectedOffset) > 0.01f
            || !copy.GlobalBasis.GetRotationQuaternion().IsEqualApprox(source.GlobalBasis.GetRotationQuaternion())
            || !copy.GlobalBasis.Scale.IsEqualApprox(source.GlobalBasis.Scale))
            throw new InvalidOperationException($"Copy '{copy.State.DisplayName}' did not preserve the source transform and placement offset.");
    }

    private string GetCopyDisplayNameForSmoke(string rootName, int expectedOrdinal)
    {
        WorldActorNode? root = _actors.FirstOrDefault(actor => actor.LegacyName == rootName);
        return $"{root?.State.DisplayName ?? rootName}-{expectedOrdinal}";
    }
}
