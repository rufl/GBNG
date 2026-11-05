using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private sealed record BuiltInProjectTemplate(string Id, string Title, string Description, string FileName);

    private static readonly BuiltInProjectTemplate[] BuiltInProjectTemplates =
    [
        new("tutorial", "Tutorial", "Start the original tutorial world in Play mode.", "tutorial"),
        new("empty", "Empty", "A clean world to build from scratch.", "template-empty"),
        new("platformer", "3D Platformer", "Start with a playable platforming scene.", "template-platformer"),
        new("fps", "First-Person Shooter", "A ready-to-edit first-person shooter.", "template-fps"),
        new("driving", "Driving", "Begin with a drivable game.", "template-driving")
    ];

    private const string BuiltInProjectDirectory = "res://Assets/StreamingAssets/ExampleGames/Public";
    private readonly List<(BuiltInProjectTemplate Template, Button Button, TextureRect Preview)> _projectTemplateCards = new();
    private Control _templatePicker = null!;

    private Control BuildTemplatePicker()
    {
        var overlay = new ColorRect
        {
            Name = "ProjectTemplatePicker",
            Color = new Color(0.025f, 0.04f, 0.06f, 0.88f),
            AnchorRight = 1f,
            AnchorBottom = 1f,
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false
        };
        var center = new CenterContainer
        {
            Name = "ProjectTemplatePickerCenter",
            AnchorRight = 1f,
            AnchorBottom = 1f
        };
        overlay.AddChild(center);

        var panel = new PanelContainer
        {
            Name = "ProjectTemplatePickerPanel",
            CustomMinimumSize = new Vector2(1000f, 660f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        panel.AddThemeStyleboxOverride("panel", MakePanelStyle("#1b2734", "#566e83"));
        center.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 18);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        panel.AddChild(margin);

        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 10);
        margin.AddChild(content);

        var heading = new HBoxContainer();
        var headingTitle = CreateLabel("Choose a starter project", 26);
        headingTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        heading.AddChild(headingTitle);
        var close = new Button { Text = "Back", CustomMinimumSize = new Vector2(84f, 36f) };
        close.Pressed += () => _templatePicker.Visible = false;
        heading.AddChild(close);
        content.AddChild(heading);
        var warning = CreateLabel("Choosing a template replaces the current editor contents. Save first if needed.", 14);
        warning.AutowrapMode = TextServer.AutowrapMode.Word;
        content.AddChild(warning);

        var scroll = new ScrollContainer
        {
            Name = "ProjectTemplateScroll",
            CustomMinimumSize = new Vector2(0f, 470f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        content.AddChild(scroll);
        var grid = new GridContainer
        {
            Name = "ProjectTemplateGrid",
            Columns = 3,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 12);
        scroll.AddChild(grid);

        _projectTemplateCards.Clear();
        foreach (BuiltInProjectTemplate template in BuiltInProjectTemplates)
        {
            string previewPath = $"{BuiltInProjectDirectory}/{template.FileName}.png";
            Texture2D previewTexture = ResourceLoader.Load<Texture2D>(previewPath)
                ?? throw new InvalidOperationException($"The built-in project preview is missing: {previewPath}");
            var card = new PanelContainer
            {
                Name = $"TemplateCard_{template.Id}",
                CustomMinimumSize = new Vector2(286f, 206f),
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            card.AddThemeStyleboxOverride("panel", MakePanelStyle("#202d3a", "#455b70"));
            grid.AddChild(card);

            var cardMargin = new MarginContainer();
            cardMargin.AddThemeConstantOverride("margin_left", 8);
            cardMargin.AddThemeConstantOverride("margin_top", 7);
            cardMargin.AddThemeConstantOverride("margin_right", 8);
            cardMargin.AddThemeConstantOverride("margin_bottom", 7);
            card.AddChild(cardMargin);
            var cardContent = new VBoxContainer();
            cardContent.AddThemeConstantOverride("separation", 4);
            cardMargin.AddChild(cardContent);

            var preview = new TextureRect
            {
                Name = $"TemplatePreview_{template.Id}",
                Texture = previewTexture,
                CustomMinimumSize = new Vector2(0f, 92f),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
            };
            cardContent.AddChild(preview);
            var title = CreateLabel(template.Title, 17);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.AutowrapMode = TextServer.AutowrapMode.Word;
            cardContent.AddChild(title);
            var description = CreateLabel(template.Description, 13);
            description.AutowrapMode = TextServer.AutowrapMode.Word;
            cardContent.AddChild(description);
            var choose = new Button
            {
                Name = $"ChooseTemplate_{template.Id}",
                Text = template.Id == "tutorial" ? "Start tutorial" : "Use this template",
                CustomMinimumSize = new Vector2(0f, 34f),
                TooltipText = template.Description
            };
            choose.Pressed += () => LoadProjectTemplate(template);
            cardContent.AddChild(choose);
            _projectTemplateCards.Add((template, choose, preview));
        }

        _templatePicker = overlay;
        return overlay;
    }

    private void ShowTemplatePicker()
    {
        _templatePicker.Visible = true;
    }

    private void LoadProjectTemplate(BuiltInProjectTemplate template)
    {
        string projectPath = $"{BuiltInProjectDirectory}/{template.FileName}.voos";
        if (!Godot.FileAccess.FileExists(projectPath))
        {
            ShowError($"The built-in project template is missing:\n{projectPath}");
            return;
        }
        if (_playMode)
            SetPlayMode(false);
        LoadProject(projectPath);
        if (template.Id == "tutorial"
            && string.Equals(_document?.SourcePath, projectPath, StringComparison.Ordinal))
            SetPlayMode(true);
    }
}
