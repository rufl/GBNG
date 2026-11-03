using System.Text.Json;
using System.Text.Json.Nodes;
using GBNG.Behaviors;
using GBNG.Domain;
using GBNG.Networking;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private readonly HashSet<string> _unsharedAssetUris = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _pendingAssetUrisByTransferId = new();
    private string? _retainedHostAssetProjectPath;
    private string? _retainedHostAssetsSourcePath;
    private void ResetCollaborativeSnapshot()
    {
        _collaborationSnapshot = _document is null ? null : CaptureCollaborativeSnapshot();
    }

    private JsonObject CaptureCollaborativeSnapshot()
    {
        foreach (WorldActorNode actor in _actors)
            actor.SaveTransform();
        if (_terrain is not null)
        {
            _terrain.FlushUpdates();
            _document!.SaveTerrainPayload(_terrain.Encode());
        }
        return _document!.Root.DeepClone().AsObject();
    }

    private void NotifyCollaborativeEdits()
    {
        if (_document is null || _applyingCollaborativePatch || _playMode)
            return;

        try
        {
            JsonObject current = CaptureCollaborativeSnapshot();
            if (_collaborationSnapshot is null)
            {
                _collaborationSnapshot = current;
                PersistNetworkHostProjectCopy();
                return;
            }

            JsonArray operations = LegacyDocumentPatch.Create(_collaborationSnapshot, current);
            if (operations.Count == 0)
            {
                _collaborationSnapshot = current;
                return;
            }

            string patchJson = operations.ToJsonString();
            _collaborationSnapshot = current;
            if (_lanSession.IsHost)
            {
                PersistNetworkHostProjectCopy();
                _lanSession.BroadcastDocumentPatch(patchJson);
            }
            else if (_lanSession.IsClient)
            {
                _lanSession.SubmitDocumentPatch(patchJson);
            }
        }
        catch (Exception exception)
        {
            GD.PushError($"Could not synchronize collaborative edits: {exception}");
            _status.Text = $"Could not synchronize collaborative edits: {exception.Message}";
        }
    }

    private void PersistNetworkHostProjectCopy()
    {
        if (!_lanSession.IsHost || _document is null || _networkHostProjectPath is null)
            return;
        try
        {
            _catalog.CopyProjectAssets(_document.SourcePath, _networkHostProjectPath);
            _document.SaveCopyTo(_networkHostProjectPath);
        }
        catch (Exception exception)
        {
            GD.PushError($"Could not update the host's join-in-progress project copy: {exception}");
            _status.Text = $"Live edits are active, but a new player may receive an older project copy: {exception.Message}";
        }
    }

    private void ApplyClientDocumentPatch(long peerId, string patchJson)
    {
        if (!_lanSession.IsHost || _document is null)
            return;
        if (!LegacyDocumentPatch.TryParse(patchJson, out JsonArray operations, out string error))
        {
            _lanSession.RejectDocumentPatch(peerId, error);
            return;
        }
        ApplyCollaborativePatch(operations, peerId);
    }

    private void ApplyNetworkDocumentPatch(string patchJson)
    {
        if (!_lanSession.IsClient || _document is null)
            return;
        if (!LegacyDocumentPatch.TryParse(patchJson, out JsonArray operations, out string error))
        {
            GD.PushWarning($"Ignored an invalid host collaborative edit: {error}");
            _status.Text = $"Ignored an invalid host edit: {error}";
            return;
        }
        ApplyCollaborativePatch(operations, senderPeerId: null);
    }

    private void ApplyCollaborativePatch(JsonArray operations, long? senderPeerId)
    {
        if (_document is null)
            return;

        try
        {
            if (_terrainStrokeActive)
                EndTerrainStroke();

            JsonObject candidate = _document.Root.DeepClone().AsObject();
            var changedRootProperties = new HashSet<string>(StringComparer.Ordinal);
            LegacyDocumentPatch.ApplyDocumentOperations(candidate, operations, changedRootProperties);
            bool actorsChanged = operations.OfType<JsonObject>()
                .Any(operation => LegacySaveDocument.ReadString(operation["scope"]) == "actor");
            string? actorStatesJson = null;
            if (actorsChanged)
            {
                if (candidate["voosEngineState"] is not JsonObject engineState
                    || engineState["actors"] is not JsonArray actors)
                    throw new InvalidDataException("The collaborative edit targets a project with no actor list.");
                LegacyDocumentPatch.ApplyActorOperations(actors, operations);
                string[] serializedActors = actors
                    .Select(actor => actor?.ToJsonString() ?? throw new InvalidDataException("The collaborative actor list contains a null value."))
                    .ToArray();
                actorStatesJson = JsonSerializer.Serialize(serializedActors);
            }

            _applyingCollaborativePatch = true;
            try
            {
                LegacyDocumentPatch.ApplyDocumentOperations(_document.Root, operations, changedRootProperties);
                if (actorStatesJson is not null)
                    ApplyNetworkActorStructure(actorStatesJson);

                if (changedRootProperties.Contains("stage") || changedRootProperties.Contains("gridState"))
                    RebuildNetworkEnvironment();
                if (changedRootProperties.Overlaps(["stage", "gridState", "behaviorDatabase", "sojoDatabase"]))
                    RebuildBehaviorRuntimeAfterCollaborativeEdit();
                RefreshCollaborativeEditorWindows();
            }
            finally
            {
                _applyingCollaborativePatch = false;
            }

            ResetCollaborativeSnapshot();
            if (_lanSession.IsHost)
            {
                PersistNetworkHostProjectCopy();
                _lanSession.BroadcastDocumentPatch(operations.ToJsonString());
            }
            _status.Text = "Collaborative edit applied.";
        }
        catch (Exception exception)
        {
            string message = $"Could not apply a collaborative edit: {exception.Message}";
            GD.PushError($"{message}\n{exception}");
            _status.Text = message;
            if (senderPeerId is long peerId)
                _lanSession.RejectDocumentPatch(peerId, message);
        }
    }

    private void RebuildNetworkEnvironment()
    {
        if (_terrain is not null && IsInstanceValid(_terrain))
        {
            if (_terrain.GetParent() is Node parent)
                parent.RemoveChild(_terrain);
            _terrain.QueueFree();
            _terrain = null;
        }
        if (_stageGroundMesh is not null && IsInstanceValid(_stageGroundMesh))
        {
            _stageGroundMesh.GetParent()?.RemoveChild(_stageGroundMesh);
            _stageGroundMesh.QueueFree();
        }
        if (_stageGroundBody is not null && IsInstanceValid(_stageGroundBody))
        {
            _stageGroundBody.GetParent()?.RemoveChild(_stageGroundBody);
            _stageGroundBody.QueueFree();
        }
        _stageGroundMesh = null;
        _stageGroundBody = null;
        _terrainStrokeBefore.Clear();
        _terrainStrokeActive = false;
        AddStageGround(_document?.Stage);
        ApplyStageVisuals();

        LegacyTerrainData? terrainData = LegacyTerrainData.Decode(_document?.GridState);
        if (terrainData is null)
            return;
        _terrain = LegacyTerrainNode.Create(terrainData);
        _world.AddChild(_terrain);
        if (_stageGroundMesh is not null)
            _stageGroundMesh.Visible = false;
    }

    private void RebuildBehaviorRuntimeAfterCollaborativeEdit()
    {
        if (_document is null)
            return;
        try
        {
            LegacyBehaviorRuntime updatedRuntime = CreateBehaviorRuntime(_document);
            _behaviorRuntime?.Dispose();
            _behaviorRuntime = updatedRuntime;
            _behaviorRuntimeError = null;
            _behaviorUi.SetCommands(new JsonArray());
            if (_selectedActor is not null)
                SelectActor(_selectedActor);
        }
        catch (Exception exception)
        {
            _behaviorRuntimeError = exception.Message;
            GD.PushError($"Could not reload behavior runtime after a collaborative edit: {exception}");
            _status.Text = $"Collaborative edit applied, but the behavior runtime rejected it: {exception.Message}";
        }
    }

    private void RefreshCollaborativeEditorWindows()
    {
        foreach (LegacyBehaviorAuthoringWindow window in Descendants<LegacyBehaviorAuthoringWindow>(this))
            window.RefreshFromDocument();
        foreach (LegacyPanelCanvasWindow window in Descendants<LegacyPanelCanvasWindow>(this))
            window.RefreshFromDocument();
        foreach (LegacyCardLibraryManagerWindow window in Descendants<LegacyCardLibraryManagerWindow>(this))
            window.RefreshFromDocument();
    }

    private static IEnumerable<T> Descendants<T>(Node root) where T : Node
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is T match)
                yield return match;
            foreach (T descendant in Descendants<T>(child))
                yield return descendant;
        }
    }
    private void ApplyClientAssetBundle(long peerId, long transferId, string bundleProjectPath)
    {
        try
        {
            if (!_lanSession.IsHost || _document is null || _networkHostProjectPath is null)
                throw new InvalidOperationException("The host no longer has a writable shared project.");

            MergeAssetBundleIntoEditor(bundleProjectPath);
            PersistNetworkHostProjectCopy();
            _lanSession.BroadcastAssetBundle(_networkHostProjectPath, peerId, transferId);
            _status.Text = "Imported model shared with the host; syncing it to connected players.";
        }
        catch (Exception exception)
        {
            _lanSession.RejectAssetBundle(peerId, transferId, $"Could not merge the imported model: {exception.Message}");
        }
        finally
        {
            DeleteExtractedAssetBundle(bundleProjectPath);
        }
    }

    private void ApplyNetworkAssetBundle(long originPeerId, long originTransferId, string bundleProjectPath)
    {
        try
        {
            if (_document is null)
                throw new InvalidOperationException("No project is open to receive the shared model.");
            MergeAssetBundleIntoEditor(bundleProjectPath);
            if (_lanSession.ConfirmAssetBundleApplied(originPeerId, originTransferId)
                && _pendingAssetUrisByTransferId.Remove(originTransferId, out string? uri))
                _unsharedAssetUris.Remove(uri);
            _status.Text = "Shared project models synchronized.";
        }
        catch (Exception exception)
        {
            GD.PushError($"Could not apply a shared project asset bundle: {exception}");
            _status.Text = $"Could not apply a shared project asset bundle: {exception.Message}";
        }
        finally
        {
            DeleteExtractedAssetBundle(bundleProjectPath);
        }
    }

    private void MergeAssetBundleIntoEditor(string bundleProjectPath)
    {
        if (_document is null)
            throw new InvalidOperationException("No project is open to receive the shared model.");

        if (_lanSession.IsHost)
        {
            string hostProjectPath = _networkHostProjectPath
                ?? throw new InvalidOperationException("The host's writable project copy is unavailable.");
            _catalog.CopyProjectAssets(bundleProjectPath, hostProjectPath);
            if (!_document.SourcePath.StartsWith("res://", StringComparison.Ordinal))
                _catalog.CopyProjectAssets(bundleProjectPath, _document.SourcePath);
            _catalog.LoadProjectAssets(hostProjectPath);
        }
        else
        {
            _catalog.CopyProjectAssets(bundleProjectPath, _document.SourcePath);
            _catalog.LoadProjectAssets(_document.SourcePath);
        }
        PopulateAssetPicker();
    }

    private static void DeleteExtractedAssetBundle(string bundleProjectPath)
    {
        string? bundleRoot = Path.GetDirectoryName(bundleProjectPath);
        if (bundleRoot is not null && Directory.Exists(bundleRoot))
            Directory.Delete(bundleRoot, recursive: true);
    }

    private void OnLanSessionStateChanged()
    {
        if (_lanSession.IsClient)
            return;
        _unsharedAssetUris.Clear();
        _pendingAssetUrisByTransferId.Clear();
    }
    private void OnClientProjectReady(long peerId)
    {
        if (!_lanSession.IsHost || _document is null)
            return;
        _lanSession.UpdateActorSnapshot(_actors);

        try
        {
            _collaborationSnapshot = CaptureCollaborativeSnapshot();
            PersistNetworkHostProjectCopy();
            JsonArray snapshot = LegacyDocumentPatch.CreateSnapshot(_document.Root);
            if (!_lanSession.SendDocumentSnapshot(peerId, snapshot.ToJsonString()))
                _status.Text = "A joining player may need a fresh project copy because its live snapshot exceeded the transfer limit.";
        }
        catch (Exception exception)
        {
            GD.PushError($"Could not send the current project state to a joining player: {exception}");
            _status.Text = $"Could not send the current project state to a joining player: {exception.Message}";
        }

        if (_networkHostProjectPath is string hostProjectPath)
            _lanSession.SendAssetBundleToPeer(hostProjectPath, peerId);
    }

    private string GetProjectAssetSourcePath()
    {
        if (_lanSession.IsHost && _networkHostProjectPath is not null)
            return _networkHostProjectPath;
        if (_retainedHostAssetsSourcePath is not null
            && PathEquals(_retainedHostAssetsSourcePath, _document!.SourcePath)
            && _retainedHostAssetProjectPath is not null)
            return _retainedHostAssetProjectPath;
        return _document!.SourcePath;
    }

    private void PreserveHostProjectAssetsBeforeLeave(string hostProjectPath, bool wasHost)
    {
        bool isCurrentHostCopy = _networkHostProjectPath is not null && PathEquals(_networkHostProjectPath, hostProjectPath);
        if (!wasHost)
        {
            if (isCurrentHostCopy)
                _networkHostProjectPath = null;
            return;
        }

        try
        {
            if (_document?.SourcePath.StartsWith("res://", StringComparison.Ordinal) == true)
            {
                if (HasProjectAssetManifest(hostProjectPath))
                    RetainHostAssets(hostProjectPath, _document.SourcePath);
                else
                {
                    DeleteRetainedHostAssetCache();
                    _catalog.LoadProjectAssets(_document.SourcePath);
                }
            }
            else
            {
                DeleteRetainedHostAssetCache();
                if (_document is not null)
                    _catalog.LoadProjectAssets(_document.SourcePath);
            }
        }
        catch (Exception exception)
        {
            GD.PushError($"Could not preserve project assets after hosting: {exception}");
            _status.Text = $"Could not preserve project assets after hosting: {exception.Message}";
        }

        if (isCurrentHostCopy)
            _networkHostProjectPath = null;
    }

    private void RetainHostAssets(string sourceProjectPath, string sourceDocumentPath)
    {
        string root = ProjectSettings.GlobalizePath("user://network-assets/offline");
        Directory.CreateDirectory(root);
        string retainedProjectPath = Path.Combine(root, $"host-{Guid.NewGuid():N}.voos");
        _catalog.CopyProjectAssets(sourceProjectPath, retainedProjectPath);
        DeleteProjectAssetSidecar(_retainedHostAssetProjectPath);
        _retainedHostAssetProjectPath = retainedProjectPath;
        _retainedHostAssetsSourcePath = sourceDocumentPath;
        _catalog.LoadProjectAssets(retainedProjectPath);
    }

    private void ReleaseRetainedHostAssetsAfterSave()
    {
        if (_lanSession.IsHost
            || _retainedHostAssetsSourcePath is null
            || _document is null
            || PathEquals(_retainedHostAssetsSourcePath, _document.SourcePath))
            return;
        DeleteRetainedHostAssetCache();
    }

    private void DiscardRetainedHostAssetsForDifferentProject(string sourcePath)
    {
        if (_retainedHostAssetsSourcePath is null || PathEquals(_retainedHostAssetsSourcePath, sourcePath))
            return;
        DeleteRetainedHostAssetCache();
    }

    private void DeleteRetainedHostAssetCache()
    {
        DeleteProjectAssetSidecar(_retainedHostAssetProjectPath);
        _retainedHostAssetProjectPath = null;
        _retainedHostAssetsSourcePath = null;
    }

    private static void DeleteProjectAssetSidecar(string? projectPath)
    {
        if (projectPath is null)
            return;
        string sidecarPath = Path.Combine(
            Path.GetDirectoryName(projectPath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(projectPath)}.assets");
        if (Directory.Exists(sidecarPath))
            Directory.Delete(sidecarPath, recursive: true);
    }

    private static bool HasProjectAssetManifest(string projectPath)
    {
        string sidecarPath = Path.Combine(
            Path.GetDirectoryName(projectPath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(projectPath)}.assets");
        return File.Exists(Path.Combine(sidecarPath, "manifest.json"));
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
