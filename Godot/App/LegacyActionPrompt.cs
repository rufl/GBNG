using System.Text.Json.Nodes;
using GBNG.Domain;
using Godot;

namespace GBNG.App;

public sealed partial class LegacyActionPrompt : Control
{
    private readonly PanelContainer _action1Panel = new();
    private readonly PanelContainer _action2Panel = new();
    private readonly Label _action1Binding = new();
    private readonly Label _action2Binding = new();
    private readonly Label _action1Description = new();
    private readonly Label _action2Description = new();
    private JsonArray _playerToolTips = new();
    private string _playerName = string.Empty;
    private bool _playMode;
    private string _action1Text = string.Empty;
    private string _action2Text = string.Empty;

    public LegacyActionPrompt()
    {
        Name = "PlayerActionPrompts";
        AnchorLeft = 0.5f;
        AnchorTop = 1f;
        AnchorRight = 0.5f;
        AnchorBottom = 1f;
        OffsetLeft = -250f;
        OffsetTop = -92f;
        OffsetRight = 250f;
        OffsetBottom = -16f;
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;

        var layout = new HBoxContainer { AnchorRight = 1f, AnchorBottom = 1f, MouseFilter = MouseFilterEnum.Ignore };
        layout.AddThemeConstantOverride("separation", 12);
        AddChild(layout);
        AddPrompt(layout, _action1Panel, _action1Binding, _action1Description, "Action1Prompt", "LMB");
        AddPrompt(layout, _action2Panel, _action2Binding, _action2Description, "Action2Prompt", "RMB");
    }

    public void SetPlayMode(bool enabled)
    {
        _playMode = enabled;
        if (!enabled)
        {
            _playerToolTips = new JsonArray();
            _playerName = string.Empty;
        }
        RefreshPrompts();
    }

    public void SetPrompts(JsonArray prompts, string playerName)
    {
        _playerToolTips = (JsonArray)prompts.DeepClone();
        _playerName = playerName;
        RefreshPrompts();
    }

    private void RefreshPrompts()
    {
        string action1 = string.Empty;
        string action2 = string.Empty;
        if (_playMode && _playerName.Length > 0)
        {
            foreach (JsonNode? item in _playerToolTips)
            {
                if (item is not JsonObject prompt
                    || LegacySaveDocument.ReadString(prompt["playerName"]) != _playerName)
                    continue;

                string text = LegacySaveDocument.ReadString(prompt["text"]);
                switch (LegacySaveDocument.ReadString(prompt["keyCode"]))
                {
                    case "action1":
                        action1 = text;
                        break;
                    case "action2":
                        action2 = text;
                        break;
                }
            }
        }

        SetPromptText(action1, action2);
    }

    private void SetPromptText(string action1, string action2)
    {
        if (_action1Text != action1)
        {
            _action1Text = action1;
            _action1Description.Text = action1;
            _action1Panel.Visible = action1.Length > 0;
        }
        if (_action2Text != action2)
        {
            _action2Text = action2;
            _action2Description.Text = action2;
            _action2Panel.Visible = action2.Length > 0;
        }
        Visible = _playMode && (_action1Text.Length > 0 || _action2Text.Length > 0);
    }

    private static void AddPrompt(
        HBoxContainer parent,
        PanelContainer panel,
        Label binding,
        Label description,
        string name,
        string bindingText)
    {
        panel.Name = name;
        panel.CustomMinimumSize = new Vector2(230f, 58f);
        panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        panel.Visible = false;
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.1f, 0.14f, 0.94f),
            BorderColor = new Color(0.35f, 0.53f, 0.68f, 0.95f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8
        });

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        panel.AddChild(margin);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        margin.AddChild(row);
        binding.Text = bindingText;
        binding.Name = $"{name}Binding";
        binding.CustomMinimumSize = new Vector2(54f, 0f);
        binding.HorizontalAlignment = HorizontalAlignment.Center;
        binding.VerticalAlignment = VerticalAlignment.Center;
        binding.AddThemeColorOverride("font_color", new Color(0.68f, 0.86f, 1f));
        row.AddChild(binding);
        description.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        description.Name = $"{name}Text";
        description.VerticalAlignment = VerticalAlignment.Center;
        description.AutowrapMode = TextServer.AutowrapMode.Word;
        row.AddChild(description);
        parent.AddChild(panel);
    }
}
