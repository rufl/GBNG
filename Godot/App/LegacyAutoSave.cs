using System.Globalization;
using Godot;
using GBNG.Domain;
using GBNG.World;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private const int AutoSaveSlotCount = 10;
    private const double AutoSaveIntervalSeconds = 60d;
    private string _autoSaveDirectory = string.Empty;
    private Godot.Timer? _autoSaveTimer;
    private FileDialog? _autoSaveRecoveryDialog;
    private int _nextAutoSaveSlot;
    private bool _autoSavesDisabled;

    private void InitializeAutoSave()
    {
        _autoSaveDirectory = ProjectSettings.GlobalizePath("user://autosaves");
        try
        {
            Directory.CreateDirectory(_autoSaveDirectory);
            _nextAutoSaveSlot = FindNextAutoSaveSlot();
            _autoSaveRecoveryDialog = new FileDialog
            {
                Name = "AutoSaveRecoveryDialog",
                Title = "Recover an auto-save",
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                CurrentDir = _autoSaveDirectory,
                UseNativeDialog = true
            };
            _autoSaveRecoveryDialog.AddFilter("autosave-*.voos", "GBNG recovery copies");
            _autoSaveRecoveryDialog.FileSelected += LoadProject;
            AddChild(_autoSaveRecoveryDialog);

            _autoSaveTimer = new Godot.Timer
            {
                Name = "AutoSaveTimer",
                WaitTime = AutoSaveIntervalSeconds,
                OneShot = false,
                IgnoreTimeScale = true
            };
            _autoSaveTimer.Timeout += OnAutoSaveTimeout;
            AddChild(_autoSaveTimer);
            ResetAutoSaveTimer();
        }
        catch (Exception exception)
        {
            _autoSavesDisabled = true;
            GD.PushError($"Could not initialize auto-save recovery: {exception}");
            _status.Text = $"Auto-save recovery is unavailable: {exception.Message}";
        }
    }

    private void ResetAutoSaveTimer()
    {
        if (_autoSaveTimer is null || _autoSavesDisabled)
            return;
        _autoSaveTimer.Stop();
        if (_document is not null)
        {
            _nextAutoSaveSlot = FindNextAutoSaveSlot(GetProtectedAutoSaveSlot());
            _autoSaveTimer.Start();
        }
    }

    private void OpenAutoSaveRecovery()
    {
        if (_autoSaveRecoveryDialog is null)
        {
            _status.Text = "Auto-save recovery is unavailable on this device.";
            return;
        }
        if (!Directory.EnumerateFiles(_autoSaveDirectory, "autosave-*.voos").Any())
        {
            _status.Text = "No auto-save recovery copies are available yet.";
            return;
        }
        _autoSaveRecoveryDialog.CurrentDir = _autoSaveDirectory;
        _autoSaveRecoveryDialog.PopupCentered(new Vector2I(1000, 700));
    }

    private void OnAutoSaveTimeout()
    {
        if (_autoSavesDisabled || _document is null || _lanSession.IsClient)
            return;
        try
        {
            string path = SaveAutoSaveSnapshot();
            _status.Text = $"Auto-saved recovery copy: {Path.GetFileName(path)}.";
        }
        catch (Exception exception)
        {
            _autoSavesDisabled = true;
            _autoSaveTimer?.Stop();
            GD.PushError($"Auto-save recovery was disabled after a failure: {exception}");
            ShowError($"Automatic recovery copies are disabled until restart.\n{exception.Message}");
        }
    }

    private string SaveAutoSaveSnapshot()
    {
        LegacySaveDocument document = _document ?? throw new InvalidOperationException("No project is open for auto-save.");
        int slot = _nextAutoSaveSlot;
        string slotPrefix = $"autosave-{slot:00}-";
        string sourceName = Path.GetFileNameWithoutExtension(document.SourcePath.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(sourceName))
            sourceName = "project";
        char[] invalidFileNameCharacters = Path.GetInvalidFileNameChars();
        char[] safeName = sourceName.ToCharArray();
        for (int index = 0; index < safeName.Length; index++)
        {
            if (Array.IndexOf(invalidFileNameCharacters, safeName[index]) >= 0 || char.IsControl(safeName[index]))
                safeName[index] = '_';
        }

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
        string fileName = $"{slotPrefix}{new string(safeName)}-{timestamp}.voos";
        string destinationPath = Path.Combine(_autoSaveDirectory, fileName);
        if (File.Exists(destinationPath) || Directory.Exists(GetAutoSaveAssetDirectory(destinationPath)))
            destinationPath = Path.Combine(_autoSaveDirectory, $"{slotPrefix}{new string(safeName)}-{timestamp}-{Guid.NewGuid():N}.voos");
        Directory.CreateDirectory(_autoSaveDirectory);

        var transforms = new Dictionary<string, Transform3D>(StringComparer.Ordinal);
        foreach (WorldActorNode actor in _actors)
        {
            actor.SaveTransform();
            transforms.Add(actor.LegacyName, actor.GlobalTransform);
        }
        document.SaveActorTransforms(transforms);
        if (_terrain is not null)
        {
            _terrain.FlushUpdates();
            document.SaveTerrainPayload(_terrain.Encode());
        }

        string destinationAssetDirectory = GetAutoSaveAssetDirectory(destinationPath);
        try
        {
            _catalog.CopyProjectAssets(GetProjectAssetSourcePath(), destinationPath);
            document.SaveCopyTo(destinationPath);
        }
        catch
        {
            if (Directory.Exists(destinationAssetDirectory))
                Directory.Delete(destinationAssetDirectory, recursive: true);
            throw;
        }

        foreach (string previousPath in Directory.GetFiles(_autoSaveDirectory, $"{slotPrefix}*.voos"))
        {
            if (PathsEqual(previousPath, destinationPath) || PathsEqual(previousPath, document.SourcePath))
                continue;
            File.Delete(previousPath);
            string previousAssetDirectory = GetAutoSaveAssetDirectory(previousPath);
            if (Directory.Exists(previousAssetDirectory))
                Directory.Delete(previousAssetDirectory, recursive: true);
        }
        _nextAutoSaveSlot = FindNextAutoSaveSlot(GetProtectedAutoSaveSlot());
        return destinationPath;
    }

    private int FindNextAutoSaveSlot(int protectedSlot = -1)
    {
        DateTime oldestSlotTime = DateTime.MaxValue;
        int oldestSlot = -1;
        for (int slot = 0; slot < AutoSaveSlotCount; slot++)
        {
            if (slot == protectedSlot)
                continue;
            string[] paths = Directory.GetFiles(_autoSaveDirectory, $"autosave-{slot:00}-*.voos");
            if (paths.Length == 0)
                return slot;
            DateTime newestSlotWrite = paths.Max(File.GetLastWriteTimeUtc);
            if (newestSlotWrite < oldestSlotTime)
            {
                oldestSlotTime = newestSlotWrite;
                oldestSlot = slot;
            }
        }
        return oldestSlot >= 0
            ? oldestSlot
            : throw new InvalidOperationException("No auto-save slot is available outside the active recovery copy.");
    }

    private int GetProtectedAutoSaveSlot()
    {
        if (_document is null || !IsAutoSavePath(_document.SourcePath))
            return -1;
        for (int slot = 0; slot < AutoSaveSlotCount; slot++)
        {
            string[] paths = Directory.GetFiles(_autoSaveDirectory, $"autosave-{slot:00}-*.voos");
            if (paths.Any(path => PathsEqual(path, _document.SourcePath)))
                return slot;
        }
        return -1;
    }

    private bool IsAutoSavePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith("res://", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(_autoSaveDirectory))
            return false;
        string fullPath = Path.GetFullPath(path);
        string relativePath = Path.GetRelativePath(_autoSaveDirectory, fullPath);
        return !Path.IsPathRooted(relativePath)
            && relativePath != ".."
            && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && Path.GetFileName(fullPath).StartsWith("autosave-", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAutoSaveAssetDirectory(string projectPath)
        => Path.Combine(
            Path.GetDirectoryName(projectPath) ?? throw new InvalidOperationException("Auto-save path has no directory."),
            $"{Path.GetFileNameWithoutExtension(projectPath)}.assets");

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
