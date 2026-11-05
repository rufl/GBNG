using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunProjectTemplatePickerSmoke()
    {
        int exitCode = 0;
        try
        {
            await WaitForUiFrames(2);
            if (_document is not null || !_welcome.Visible)
                throw new InvalidOperationException("A smoke flag without a project path must leave the welcome screen ready for a new project.");
            var newProjectButton = _welcome.FindChild("NewProjectButton", recursive: true, owned: false) as Button
                ?? throw new InvalidOperationException("The welcome screen is missing its New project button.");
            if (!newProjectButton.IsVisibleInTree() || newProjectButton.Disabled)
                throw new InvalidOperationException("The welcome screen does not visibly offer project creation.");
            string welcomeScreenshot = ProjectSettings.GlobalizePath("res://.godot/project-template-welcome-smoke.png");
            Directory.CreateDirectory(Path.GetDirectoryName(welcomeScreenshot)!);
            if (GetViewport().GetTexture().GetImage().SavePng(welcomeScreenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the project-template welcome screenshot.");
            newProjectButton.EmitSignal(BaseButton.SignalName.Pressed);
            await WaitForUiFrames(2);

            if (!_templatePicker.Visible
                || !_templatePicker.IsVisibleInTree()
                || _projectTemplateCards.Count != 5
                || _projectTemplateCards.Any(card => card.Preview.Texture is null || !card.Button.IsVisibleInTree()))
                throw new InvalidOperationException("The template picker did not show all five built-in projects and previews.");

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/project-template-picker-smoke.png");
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            if (GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the project-template picker screenshot.");

            foreach (var card in _projectTemplateCards)
            {
                ShowTemplatePicker();
                card.Button.EmitSignal(BaseButton.SignalName.Pressed);
                await WaitForUiFrames(2);
                string expectedPath = $"{BuiltInProjectDirectory}/{card.Template.FileName}.voos";
                bool shouldPlay = card.Template.Id == "tutorial";
                if (_document is null
                    || !string.Equals(_document.SourcePath, expectedPath, StringComparison.Ordinal)
                    || _welcome.Visible
                    || !_editor.Visible
                    || _templatePicker.Visible
                    || _playMode != shouldPlay)
                    throw new InvalidOperationException($"Selecting the '{card.Template.Title}' template did not launch its expected editor/play state.");
            }

            GD.Print($"PROJECT_TEMPLATE_PICKER_SMOKE_OK templates={_projectTemplateCards.Count} previews=true createFromWelcome=true tutorialPlay=true editorLoad=true welcome={welcomeScreenshot} picker={screenshot}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"PROJECT_TEMPLATE_PICKER_SMOKE_FAILED: {exception}");
        }
        finally
        {
            GetTree().Quit(exitCode);
        }
    }
}
