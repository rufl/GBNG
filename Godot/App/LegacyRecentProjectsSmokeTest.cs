using Godot;
using GBNG.Domain;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunRecentProjectsSmoke()
    {
        int exitCode = 0;
        string previousConfigPath = _recentProjectsConfigPath;
        string[] previousRecentPaths = _recentProjectPaths.ToArray();
        string smokeDirectory = Path.Combine(
            ProjectSettings.GlobalizePath("user://"),
            $"recent-project-smoke-{Guid.NewGuid():N}");
        try
        {
            await WaitForUiFrames(2);
            LegacySaveDocument sourceDocument = _document ?? throw new InvalidOperationException("The recent-project smoke project did not load.");
            Directory.CreateDirectory(smokeDirectory);
            _recentProjectsConfigPath = $"user://{Path.GetFileName(smokeDirectory)}/recent-projects.cfg";
            _recentProjectPaths.Clear();

            string[] projectPaths = Enumerable.Range(0, MaxRecentProjects + 1)
                .Select(index => Path.Combine(smokeDirectory, $"project-{index:00}.voos"))
                .ToArray();
            for (int index = 0; index < projectPaths.Length; index++)
            {
                if (index == MaxRecentProjects)
                    sourceDocument.SaveCopyTo(projectPaths[index]);
                else
                    File.WriteAllText(projectPaths[index], "{}");
                AddRecentProject(projectPaths[index]);
            }

            string[] expectedPaths = Enumerable.Range(1, MaxRecentProjects)
                .Reverse()
                .Select(index => projectPaths[index])
                .ToArray();
            AssertRecentProjectOrder(expectedPaths, "MRU insertion and ten-project limit");
            AddRecentProject(projectPaths[MaxRecentProjects]);
            AssertRecentProjectOrder(expectedPaths, "Duplicate recent-project insertion");

            LoadRecentProjects();
            AssertRecentProjectOrder(expectedPaths, "Recent-project persistence reload");
            File.Delete(projectPaths[1]);
            LoadRecentProjects();
            string[] afterPruning = expectedPaths[..^1];
            AssertRecentProjectOrder(afterPruning, "Missing-project pruning");

            ShowWelcome();
            await WaitForUiFrames(2);
            if (_recentProjectPicker is null
                || _openRecentProjectButton is null
                || !_recentProjectPicker.IsVisibleInTree()
                || !_openRecentProjectButton.IsVisibleInTree()
                || _recentProjectPicker.ItemCount != afterPruning.Length
                || _recentProjectPicker.Selected != 0
                || !PathsEqual(_recentProjectPicker.GetItemMetadata(0).AsString(), projectPaths[MaxRecentProjects])
                || _openRecentProjectButton.Disabled)
                throw new InvalidOperationException("The welcome screen did not visibly present the most recent project as an available choice.");

            _openRecentProjectButton.EmitSignal(BaseButton.SignalName.Pressed);
            await WaitForUiFrames(2);
            if (_document is null
                || !PathsEqual(_document.SourcePath, projectPaths[MaxRecentProjects])
                || _welcome.Visible
                || !_editor.Visible)
                throw new InvalidOperationException("Opening the selected recent project did not load its .voos project into the editor.");

            GD.Print("RECENT_PROJECTS_SMOKE_OK cap=10 mru=true persistence=true stalePruned=true uiOpen=true");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"RECENT_PROJECTS_SMOKE_FAILED: {exception}");
        }
        finally
        {
            _recentProjectsConfigPath = previousConfigPath;
            _recentProjectPaths.Clear();
            _recentProjectPaths.AddRange(previousRecentPaths);
            RefreshRecentProjectPicker();
            try
            {
                if (Directory.Exists(smokeDirectory))
                    Directory.Delete(smokeDirectory, recursive: true);
            }
            catch (Exception exception)
            {
                exitCode = 1;
                GD.PushWarning($"Could not remove recent-project smoke data: {exception.Message}");
            }
            GetTree().Quit(exitCode);
        }
    }

    private void AssertRecentProjectOrder(IReadOnlyList<string> expectedPaths, string operation)
    {
        if (_recentProjectPaths.Count != expectedPaths.Count)
            throw new InvalidOperationException($"{operation} retained {_recentProjectPaths.Count} projects instead of {expectedPaths.Count}.");
        for (int index = 0; index < expectedPaths.Count; index++)
        {
            if (!PathsEqual(_recentProjectPaths[index], expectedPaths[index]))
                throw new InvalidOperationException($"{operation} put '{_recentProjectPaths[index]}' at position {index}, expected '{expectedPaths[index]}'.");
        }
    }
}
