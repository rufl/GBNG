using Godot;
using GBNG.Domain;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunAutoSaveSmoke()
    {
        int exitCode = 0;
        string smokeDirectory = Path.Combine(
            ProjectSettings.GlobalizePath("user://"),
            $"autosave-smoke-{Guid.NewGuid():N}");
        try
        {
            await WaitForUiFrames(2);
            LegacySaveDocument document = _document ?? throw new InvalidOperationException("The auto-save smoke project did not load.");
            string originalSourcePath = document.SourcePath;
            _autoSaveDirectory = smokeDirectory;
            Directory.CreateDirectory(smokeDirectory);
            _nextAutoSaveSlot = FindNextAutoSaveSlot();

            for (int index = 0; index <= AutoSaveSlotCount; index++)
            {
                document.Root["autosaveSmokeMarker"] = index;
                SaveAutoSaveSnapshot();
            }

            string[] snapshots = Directory.GetFiles(smokeDirectory, "autosave-*.voos");
            if (snapshots.Length != AutoSaveSlotCount)
                throw new InvalidOperationException($"The rotating auto-save store retained {snapshots.Length} copies instead of {AutoSaveSlotCount}.");
            var savedMarkers = new HashSet<int>();
            foreach (string path in snapshots)
            {
                LegacySaveDocument snapshot = LegacySaveDocument.Load(path);
                savedMarkers.Add(LegacySaveDocument.ReadInt(snapshot.Root["autosaveSmokeMarker"], -1));
                if (snapshot.Actors.Count != document.Actors.Count)
                    throw new InvalidOperationException($"Auto-save '{Path.GetFileName(path)}' lost project actors.");
            }
            if (!savedMarkers.SetEquals(Enumerable.Range(1, AutoSaveSlotCount)))
                throw new InvalidOperationException("The rotating auto-save store did not replace the oldest recovery copy.");
            if (document.SourcePath != originalSourcePath || !IsAutoSavePath(snapshots[0]))
                throw new InvalidOperationException("Auto-save recovery changed the original project path or failed to identify a recovery copy.");

            string protectedRecoveryPath = snapshots[0];
            _document = LegacySaveDocument.Load(protectedRecoveryPath);
            int protectedMarker = LegacySaveDocument.ReadInt(_document.Root["autosaveSmokeMarker"], -1);
            ResetAutoSaveTimer();
            _document.Root["autosaveSmokeMarker"] = AutoSaveSlotCount + 1;
            string continuedSnapshotPath = SaveAutoSaveSnapshot();
            string[] continuedSnapshots = Directory.GetFiles(smokeDirectory, "autosave-*.voos");
            int persistedProtectedMarker = LegacySaveDocument.ReadInt(
                LegacySaveDocument.Load(protectedRecoveryPath).Root["autosaveSmokeMarker"],
                -1);
            if (continuedSnapshots.Length != AutoSaveSlotCount
                || !File.Exists(protectedRecoveryPath)
                || PathsEqual(protectedRecoveryPath, continuedSnapshotPath)
                || _document.SourcePath != protectedRecoveryPath
                || persistedProtectedMarker != protectedMarker)
                throw new InvalidOperationException("Auto-saving from a recovered project did not preserve its source recovery copy and ten-slot limit.");
            if (!continuedSnapshots.Any(path => PathsEqual(path, continuedSnapshotPath)))
                throw new InvalidOperationException("Auto-save stopped after opening a recovery copy.");

            GD.Print($"AUTOSAVE_SMOKE_OK slots={continuedSnapshots.Length} oldestReplaced=true sourcePathPreserved=true activeRecoveryPreserved=true continuedAutosaving=true recoverableVoos=true");
        }
        catch (System.Exception exception)
        {
            exitCode = 1;
            GD.PushError($"AUTOSAVE_SMOKE_FAILED: {exception}");
        }
        finally
        {
            if (Directory.Exists(smokeDirectory))
                Directory.Delete(smokeDirectory, recursive: true);
            GetTree().Quit(exitCode);
        }
    }
}
