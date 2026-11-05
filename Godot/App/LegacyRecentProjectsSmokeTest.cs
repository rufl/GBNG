using Godot;
using GBNG.Domain;
using GBNG.Networking;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunRecentProjectsSmoke()
    {
        int exitCode = 0;
        string previousConfigPath = _recentProjectsConfigPath;
        string[] previousRecentPaths = _recentProjectPaths.ToArray();
        LegacyLanNetworkWindow? previousNetworkWindow = null;
        LegacyLanNetworkWindow? smokeNetworkWindow = null;
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

            string networkSettingsPath = Path.Combine(smokeDirectory, "network-settings.cfg");
            LegacyNetworkSettings smokeSettings = LegacyNetworkSettings.Load(networkSettingsPath);
            if (smokeSettings.Save() != Error.Ok)
                throw new InvalidOperationException("Could not initialize isolated network settings for the resume smoke.");
            previousNetworkWindow = _networkWindow;
            smokeNetworkWindow = new LegacyLanNetworkWindow(
                _lanSession,
                PrepareNetworkHostProjectCopy,
                OpenReceivedNetworkProject,
                smokeSettings);
            AddChild(smokeNetworkWindow);
            _networkWindow = smokeNetworkWindow;
            _networkWindow.ResumeStateChanged += RefreshResumeButton;

            RefreshRecentProjectPicker();
            if (_resumeSessionButton is null || !_resumeSessionButton.Disabled)
                throw new InvalidOperationException("Resume was enabled with no recent project or prior online room.");

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
                || _resumeSessionButton is null
                || !_recentProjectPicker.IsVisibleInTree()
                || !_openRecentProjectButton.IsVisibleInTree()
                || _recentProjectPicker.ItemCount != afterPruning.Length
                || _recentProjectPicker.Selected != 0
                || !PathsEqual(_recentProjectPicker.GetItemMetadata(0).AsString(), projectPaths[MaxRecentProjects])
                || _openRecentProjectButton.Disabled
                || _resumeSessionButton.Disabled
                || _resumeSessionButton.Text != "Resume last project")
                throw new InvalidOperationException("The welcome screen did not offer the newest project through both recent-open and resume actions.");
            string screenshotPath = ProjectSettings.GlobalizePath("res://.godot/resume-welcome-smoke.png");
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
            if (GetViewport().GetTexture().GetImage().SavePng(screenshotPath) != Error.Ok)
                throw new InvalidOperationException("Could not save the welcome resume screenshot.");


            smokeSettings.LastJoinedRoomCode = "ab12-cd34";
            if (smokeSettings.LastJoinedRoomCode != "AB12CD34" || smokeSettings.Save() != Error.Ok)
                throw new InvalidOperationException("The last invite code was not normalized and persisted.");
            RefreshResumeButton();
            if (_resumeSessionButton.Text != "Rejoin online game")
                throw new InvalidOperationException("The resume action did not prefer the last online room.");

            _resumeSessionButton.EmitSignal(BaseButton.SignalName.Pressed);
            await WaitForUiFrames(2);
            var inviteCodeInput = smokeNetworkWindow.FindChild("InviteCode", recursive: true, owned: false) as LineEdit;
            var networkStatus = smokeNetworkWindow.FindChild("NetworkStatus", recursive: true, owned: false) as Label;
            if (!smokeNetworkWindow.Visible
                || inviteCodeInput?.Text != "AB12CD34"
                || networkStatus is null
                || !networkStatus.Text.Contains("WSS matchmaking address", StringComparison.Ordinal)
                || smokeSettings.LastJoinedRoomCode != "AB12CD34")
                throw new InvalidOperationException("Resuming the last online room did not preserve its code and expose the matchmaking setup.");

            smokeNetworkWindow.Hide();
            smokeSettings.LastJoinedRoomCode = string.Empty;
            if (smokeSettings.Save() != Error.Ok)
                throw new InvalidOperationException("Could not clear the isolated online-room resume code.");
            RefreshResumeButton();
            _recentProjectPicker.Select(1);
            _resumeSessionButton.EmitSignal(BaseButton.SignalName.Pressed);
            await WaitForUiFrames(2);
            if (_document is null
                || !PathsEqual(_document.SourcePath, projectPaths[MaxRecentProjects])
                || _welcome.Visible
                || !_editor.Visible)
                throw new InvalidOperationException("Resume did not open the newest project independently of the selected recent-project row.");

            ShowWelcome();
            await WaitForUiFrames(2);
            _openRecentProjectButton.EmitSignal(BaseButton.SignalName.Pressed);
            await WaitForUiFrames(2);
            if (_document is null
                || !PathsEqual(_document.SourcePath, projectPaths[MaxRecentProjects])
                || _welcome.Visible
                || !_editor.Visible)
                throw new InvalidOperationException("Opening the selected recent project did not load its .voos project into the editor.");

            GD.Print($"RECENT_PROJECTS_SMOKE_OK cap=10 mru=true persistence=true stalePruned=true uiOpen=true resume=online+latestProject screenshot={screenshotPath}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"RECENT_PROJECTS_SMOKE_FAILED: {exception}");
        }
        finally
        {
            if (smokeNetworkWindow is not null && IsInstanceValid(smokeNetworkWindow))
            {
                smokeNetworkWindow.ResumeStateChanged -= RefreshResumeButton;
                smokeNetworkWindow.QueueFree();
                await WaitForUiFrames(2);
            }
            if (previousNetworkWindow is not null)
                _networkWindow = previousNetworkWindow;
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
