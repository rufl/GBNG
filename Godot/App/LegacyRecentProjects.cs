using System.Text.Json;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private const int MaxRecentProjects = 10;
    private string _recentProjectsConfigPath = "user://recent-projects.cfg";
    private readonly List<string> _recentProjectPaths = new();
    private OptionButton? _recentProjectPicker;
    private Button? _openRecentProjectButton;
    private Button? _resumeSessionButton;

    private void LoadRecentProjects()
    {
        _recentProjectPaths.Clear();
        var config = new ConfigFile();
        Error loadError = config.Load(_recentProjectsConfigPath);
        if (loadError == Error.Ok)
        {
            string serializedPaths = config.GetValue("recent", "paths_json", "[]").AsString();
            try
            {
                string[] savedPaths = JsonSerializer.Deserialize<string[]>(serializedPaths) ?? Array.Empty<string>();
                foreach (string savedPath in savedPaths)
                {
                    if (string.IsNullOrWhiteSpace(savedPath) || savedPath.StartsWith("res://", StringComparison.Ordinal))
                        continue;
                    try
                    {
                        string fullPath = NormalizeRecentProjectPath(savedPath);
                        if (!File.Exists(fullPath) || _recentProjectPaths.Any(path => PathsEqual(path, fullPath)))
                            continue;
                        _recentProjectPaths.Add(fullPath);
                        if (_recentProjectPaths.Count == MaxRecentProjects)
                            break;
                    }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        GD.PushWarning($"Ignoring invalid recent project path '{savedPath}': {exception.Message}");
                    }
                }

                if (!string.Equals(serializedPaths, JsonSerializer.Serialize(_recentProjectPaths), StringComparison.Ordinal))
                    SaveRecentProjects();
            }
            catch (JsonException exception)
            {
                GD.PushWarning($"Could not read recent project history: {exception.Message}");
            }
        }
        else if (loadError != Error.FileNotFound)
        {
            GD.PushWarning($"Could not load recent project history: {loadError}.");
        }
        RefreshRecentProjectPicker();
    }

    private void AddRecentProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("res://", StringComparison.Ordinal))
            return;
        try
        {
            string fullPath = NormalizeRecentProjectPath(path);
            if (!File.Exists(fullPath) || IsAutoSavePath(fullPath))
                return;
            _recentProjectPaths.RemoveAll(existingPath => PathsEqual(existingPath, fullPath));
            _recentProjectPaths.Insert(0, fullPath);
            if (_recentProjectPaths.Count > MaxRecentProjects)
                _recentProjectPaths.RemoveRange(MaxRecentProjects, _recentProjectPaths.Count - MaxRecentProjects);
            SaveRecentProjects();
            RefreshRecentProjectPicker();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            GD.PushWarning($"Could not add recent project '{path}': {exception.Message}");
        }
    }

    private void OpenSelectedRecentProject()
    {
        if (_recentProjectPicker is null || _recentProjectPicker.Selected < 0)
            return;
        string path = _recentProjectPicker.GetItemMetadata(_recentProjectPicker.Selected).AsString();
        OpenRecentProject(path);
    }

    private void ResumeLastSession()
    {
        if (_networkWindow is not null && _networkWindow.LastJoinedRoomCode.Length == 8)
        {
            _networkWindow.ResumeLastOnlineRoom();
            return;
        }
        if (_recentProjectPaths.Count > 0)
            OpenRecentProject(_recentProjectPaths[0]);
    }

    private void OpenRecentProject(string path)
    {
        if (!File.Exists(path))
        {
            _recentProjectPaths.RemoveAll(existingPath => PathsEqual(existingPath, path));
            SaveRecentProjects();
            RefreshRecentProjectPicker();
            _status.Text = "That recent project is no longer available.";
            return;
        }
        LoadProject(path);
    }

    private void RefreshResumeButton()
    {
        if (_resumeSessionButton is null || !IsInstanceValid(_resumeSessionButton))
            return;

        string roomCode = _networkWindow?.LastJoinedRoomCode ?? string.Empty;
        if (roomCode.Length == 8)
        {
            _resumeSessionButton.Text = "Rejoin online game";
            _resumeSessionButton.TooltipText = $"Rejoin the last online game with invite code {roomCode}.";
            _resumeSessionButton.Disabled = false;
        }
        else if (_recentProjectPaths.Count > 0 && File.Exists(_recentProjectPaths[0]))
        {
            string projectName = Path.GetFileNameWithoutExtension(_recentProjectPaths[0]);
            _resumeSessionButton.Text = "Resume last project";
            _resumeSessionButton.TooltipText = $"Open {projectName}.";
            _resumeSessionButton.Disabled = false;
        }
        else
        {
            _resumeSessionButton.Text = "Resume last session";
            _resumeSessionButton.TooltipText = "Open the most recent project or rejoin the last online game.";
            _resumeSessionButton.Disabled = true;
        }
    }
    private void RefreshRecentProjectPicker()
    {
        if (_recentProjectPicker is null || !IsInstanceValid(_recentProjectPicker))
            return;
        _recentProjectPicker.Clear();
        if (_recentProjectPaths.Count == 0)
        {
            _recentProjectPicker.AddItem("No saved projects yet");
            _recentProjectPicker.Disabled = true;
            if (_openRecentProjectButton is not null && IsInstanceValid(_openRecentProjectButton))
                _openRecentProjectButton.Disabled = true;
            RefreshResumeButton();
            return;
        }

        _recentProjectPicker.Disabled = false;
        for (int index = 0; index < _recentProjectPaths.Count; index++)
        {
            string path = _recentProjectPaths[index];
            string projectName = Path.GetFileNameWithoutExtension(path);
            string directoryName = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
            string label = string.IsNullOrWhiteSpace(directoryName)
                ? projectName
                : $"{projectName} ({directoryName})";
            _recentProjectPicker.AddItem(label);
            _recentProjectPicker.SetItemMetadata(index, path);
        }
        _recentProjectPicker.Select(0);
        if (_openRecentProjectButton is not null && IsInstanceValid(_openRecentProjectButton))
            _openRecentProjectButton.Disabled = false;
        RefreshResumeButton();
    }


    private void SaveRecentProjects()
    {
        var config = new ConfigFile();
        config.SetValue("recent", "paths_json", JsonSerializer.Serialize(_recentProjectPaths));
        Error error = config.Save(_recentProjectsConfigPath);
        if (error != Error.Ok)
            GD.PushWarning($"Could not save recent project history: {error}.");
    }

    private static string NormalizeRecentProjectPath(string path)
        => path.StartsWith("user://", StringComparison.Ordinal)
            ? ProjectSettings.GlobalizePath(path)
            : Path.GetFullPath(path);
}
