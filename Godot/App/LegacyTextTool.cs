using GBNG.Domain;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private const string TextToolPlaceholder = "[insert text here]";
    private const float TextToolPanelHeight = 1f;
    private const float TextToolSpaceDistance = 15f;

    private Button _textToolToggle = null!;
    private OptionButton _textToolStylePicker = null!;
    private CheckButton _textToolSnapToggle = null!;
    private Button _textToolRotateButton = null!;
    private Control _textToolEditorOverlay = null!;
    private TextEdit _textToolEditField = null!;
    private PanelContainer _textToolHoverHint = null!;
    private Label _textToolHoverHintLabel = null!;
    private WorldActorNode? _textToolEditingActor;
    private WorldActorNode? _textToolHoverActor;
    private string _textToolOriginalText = string.Empty;
    private bool _textToolEditingCreatedActor;
    private bool _textToolActive;
    private bool _textToolBillboard = true;
    private bool _textToolSnap;
    private int _textToolManualQuarterTurns;
    private Node3D? _textToolPreview;
    private Vector3 _textToolPlacementPosition;
    private Quaternion _textToolPlacementRotation = Quaternion.Identity;
    private bool _textToolHasPlacement;

    private void AddTextToolControls(HBoxContainer toolbar)
    {
        _textToolToggle = new Button
        {
            Name = "TextToolButton",
            Text = "Text",
            TooltipText = "Place text panels; click an existing panel to edit it. Esc exits.",
            ToggleMode = true,
            CustomMinimumSize = new Vector2(70f, 36f)
        };
        _textToolToggle.Toggled += SetTextToolActive;
        toolbar.AddChild(_textToolToggle);

        _textToolStylePicker = new OptionButton
        {
            Name = "TextToolStylePicker",
            TooltipText = "Billboard text faces the camera; Panel text keeps a fixed direction.",
            CustomMinimumSize = new Vector2(125f, 36f),
            Disabled = true
        };
        _textToolStylePicker.AddItem("Billboard");
        _textToolStylePicker.AddItem("Panel");
        _textToolStylePicker.Select(0);
        _textToolStylePicker.ItemSelected += SetTextToolStyle;
        toolbar.AddChild(_textToolStylePicker);

        _textToolSnapToggle = new CheckButton
        {
            Name = "TextToolSnapToggle",
            Text = "Snap",
            TooltipText = "Snap panel placement to the legacy terrain grid.",
            Disabled = true
        };
        _textToolSnapToggle.Toggled += enabled =>
        {
            _textToolSnap = enabled;
            UpdateTextToolPreview(GetViewport().GetMousePosition());
        };
        toolbar.AddChild(_textToolSnapToggle);

        _textToolRotateButton = new Button
        {
            Name = "TextToolRotateButton",
            Text = "Rotate",
            TooltipText = "Rotate fixed panels by 90 degrees (R).",
            CustomMinimumSize = new Vector2(74f, 36f),
            Disabled = true
        };
        _textToolRotateButton.Pressed += RotateTextToolPanel;
        toolbar.AddChild(_textToolRotateButton);
    }
    private void SetTextToolStyle(long selectedIndex)
    {
        _textToolBillboard = selectedIndex == 0;
        if (_textToolRotateButton is not null)
            _textToolRotateButton.Disabled = !_textToolActive || _textToolBillboard || _textToolEditingActor is not null;
        UpdateTextToolPreview(GetViewport().GetMousePosition());
    }

    private Control BuildTextToolEditorOverlay()
    {
        var overlay = new CenterContainer
        {
            Name = "TextToolEditorOverlay",
            AnchorTop = 0.56f,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        var panel = new PanelContainer
        {
            Name = "TextToolEditorPanel",
            CustomMinimumSize = new Vector2(540f, 230f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        panel.AddThemeStyleboxOverride("panel", MakePanelStyle("#1b2734", "#6c9ac2"));
        overlay.AddChild(panel);
        var content = new VBoxContainer();
        content.AddChild(CreateLabel("Text panel", 16));
        _textToolEditField = new TextEdit
        {
            Name = "TextToolEditField",
            CustomMinimumSize = new Vector2(510f, 150f),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        _textToolEditField.TextChanged += UpdateTextToolEditorText;
        _textToolEditField.GuiInput += HandleTextToolEditorInput;
        content.AddChild(_textToolEditField);
        var actions = new HBoxContainer();
        actions.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var cancel = new Button { Name = "TextToolCancelButton", Text = "Cancel", CustomMinimumSize = new Vector2(90f, 34f) };
        cancel.Pressed += CancelTextToolEditing;
        actions.AddChild(cancel);
        var finish = new Button { Name = "TextToolFinishButton", Text = "Done", CustomMinimumSize = new Vector2(90f, 34f) };
        finish.Pressed += FinishTextToolEditing;
        actions.AddChild(finish);
        content.AddChild(actions);
        panel.AddChild(content);
        _textToolEditorOverlay = overlay;
        return overlay;
    }
    private Control BuildTextToolHoverHint()
    {
        var hint = new PanelContainer
        {
            Name = "TextToolHoverHint",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = 20,
            CustomMinimumSize = new Vector2(220f, 38f)
        };
        hint.AddThemeStyleboxOverride("panel", MakePanelStyle("#111922", "#79c9e8"));
        _textToolHoverHintLabel = CreateLabel(string.Empty, 12);
        _textToolHoverHintLabel.CustomMinimumSize = new Vector2(200f, 30f);
        _textToolHoverHintLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        hint.AddChild(_textToolHoverHintLabel);
        _textToolHoverHint = hint;
        return hint;
    }

    private void SetTextToolActive(bool active)
    {
        if (active && (_document is null || _playMode || !_editor.Visible))
        {
            _textToolToggle.SetPressedNoSignal(false);
            _status.Text = "Open a project in edit mode to use the Text tool.";
            return;
        }

        if (!active && _textToolEditingActor is not null)
            FinishTextToolEditing();

        _textToolActive = active;
        _textToolToggle.SetPressedNoSignal(active);
        _textToolStylePicker.Disabled = !active || _textToolEditingActor is not null;
        _textToolSnapToggle.Disabled = !active || _textToolEditingActor is not null;
        _textToolRotateButton.Disabled = !active || _textToolBillboard || _textToolEditingActor is not null;
        if (active)
        {
            _textToolManualQuarterTurns = 0;
            EnsureTextToolPreview();
            UpdateTextToolPreview(GetViewport().GetMousePosition());
            _status.Text = "Text tool: click the world to place a panel; click an existing text panel to edit. R rotates fixed panels.";
        }
        else
        {
            _textToolHoverActor = null;
            _textToolHasPlacement = false;
            if (_textToolPreview is not null && IsInstanceValid(_textToolPreview))
                _textToolPreview.Visible = false;
            if (_status is not null && _document is not null)
                _status.Text = "Text tool closed.";
            _textToolHoverHint.Visible = false;
        }
    }

    private void EnsureTextToolPreview()
    {
        if (_textToolPreview is not null && IsInstanceValid(_textToolPreview))
            return;

        _textToolPreview = new Node3D
        {
            Name = "TextToolPlacementPreview",
            Scale = Vector3.One * 6f,
            Visible = false
        };
        var renderer = new LegacyCommentPanelRenderer { Name = "TextToolPlacementRenderer" };
        _textToolPreview.AddChild(renderer);
        _world.AddChild(_textToolPreview);
        renderer.SetTint(new Color(0.02f, 0.03f, 0.04f, 0.42f));
        renderer.SetCommentText(TextToolPlaceholder);
    }

    private void UpdateTextToolPreview(Vector2 screenPosition)
    {
        if (!_textToolActive || _playMode || _document is null || _textToolEditingActor is not null)
        {
            if (_textToolPreview is not null && IsInstanceValid(_textToolPreview))
                _textToolPreview.Visible = false;
            _textToolHoverHint.Visible = false;
            return;
        }
        EnsureTextToolPreview();
        if (IsPointerOverUi(screenPosition))
        {
            _textToolHoverActor = null;
            _textToolHasPlacement = false;
            _textToolHoverHint.Visible = false;
            _textToolPreview!.Visible = false;
            return;
        }

        WorldActorNode? hoveredActor = GetActorAtScreenPosition(screenPosition);
        _textToolHoverActor = hoveredActor is { SupportsCommentText: true } ? hoveredActor : null;
        if (_textToolHoverActor is not null)
        {
            _textToolHasPlacement = false;
            _textToolPreview!.Visible = false;
            _textToolHoverHintLabel.Text = $"Click to edit: {_textToolHoverActor.State.DisplayName}";
            Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
            _textToolHoverHint.Position = new Vector2(
                Mathf.Clamp(screenPosition.X + 18f, 4f, Mathf.Max(4f, viewportSize.X - 224f)),
                Mathf.Clamp(screenPosition.Y + 18f, 4f, Mathf.Max(4f, viewportSize.Y - 42f)));
            _textToolHoverHint.Visible = true;
            _status.Text = $"Text panel: click to edit {_textToolHoverActor.State.DisplayName}.";
            return;
        }
        _textToolHoverHint.Visible = false;

        Vector3 rayOrigin = _camera.ProjectRayOrigin(screenPosition);
        Vector3 rayDirection = _camera.ProjectRayNormal(screenPosition);
        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(
            rayOrigin,
            rayOrigin + rayDirection * 10_000f,
            collisionMask: 3u);
        query.CollideWithAreas = false;
        Godot.Collections.Dictionary hit = _world.GetWorld3D().DirectSpaceState.IntersectRay(query);
        Vector3 surfacePosition;
        if (hit.Count > 0)
        {
            surfacePosition = hit["position"].AsVector3();
        }
        else
        {
            float distanceToGround = Mathf.Abs(rayDirection.Y) > 0.0001f
                ? -rayOrigin.Y / rayDirection.Y
                : -1f;
            surfacePosition = distanceToGround > 0f && distanceToGround <= 10_000f
                ? rayOrigin + rayDirection * distanceToGround
                : rayOrigin + rayDirection * TextToolSpaceDistance;
        }

        if (_textToolSnap)
            surfacePosition = SnapTextToolPosition(surfacePosition);
        _textToolPlacementPosition = surfacePosition + Vector3.Up * TextToolPanelHeight;
        _textToolPlacementRotation = GetTextToolPanelRotation();
        _textToolHasPlacement = true;
        _textToolPreview!.GlobalPosition = _textToolPlacementPosition;
        if (!_textToolBillboard)
            _textToolPreview.GlobalRotation = _textToolPlacementRotation.GetEuler();
        _textToolPreview.Visible = true;
        _status.Text = "Text tool: click to place a panel. Shift+Enter adds a line; Enter finishes.";
        UpdateTextToolPreviewOrientation();
    }

    private static Vector3 SnapTextToolPosition(Vector3 position)
    {
        float horizontalUnit = LegacyTerrainData.CellWidth * 0.5f;
        float verticalUnit = LegacyTerrainData.CellHeight * 0.5f;
        return new Vector3(
            Mathf.Round(position.X / horizontalUnit) * horizontalUnit,
            Mathf.Round(position.Y / verticalUnit) * verticalUnit,
            Mathf.Round(position.Z / horizontalUnit) * horizontalUnit);
    }

    private Quaternion GetTextToolPanelRotation()
    {
        if (_textToolBillboard)
            return Quaternion.Identity;
        float yaw = Mathf.Pi + _camera.GlobalRotation.Y + _textToolManualQuarterTurns * Mathf.Pi * 0.5f;
        return Quaternion.FromEuler(new Vector3(0f, yaw, 0f));
    }

    private void UpdateTextToolPreviewOrientation()
    {
        if (!_textToolActive || !_textToolHasPlacement || _textToolPreview is null || !IsInstanceValid(_textToolPreview))
            return;
        if (_textToolBillboard)
        {
            _textToolPreview.GlobalRotation = new Vector3(
                -_camera.GlobalRotation.X,
                Mathf.Pi + _camera.GlobalRotation.Y,
                0f);
        }
        else
        {
            _textToolPreview.GlobalRotation = _textToolPlacementRotation.GetEuler();
        }
    }

    private void HandleTextToolClick(Vector2 screenPosition)
    {
        UpdateTextToolPreview(screenPosition);
        if (_textToolHoverActor is { SupportsCommentText: true } hoveredActor)
        {
            BeginTextToolEditing(hoveredActor, created: false);
            return;
        }
        if (!_textToolHasPlacement || _document is null)
            return;

        string uri = _textToolBillboard
            ? "builtin:BuiltinAssets/BillboardTextPanel"
            : "builtin:BuiltinAssets/CommentPanel";
        if (!AddActor(uri, _textToolPlacementPosition, _textToolPlacementRotation, recordHistory: false)
            || _selectedActor is null)
        {
            _status.Text = "Could not create the text panel.";
            return;
        }
        BeginTextToolEditing(_selectedActor, created: true);
    }

    private void BeginTextToolEditing(WorldActorNode actor, bool created)
    {
        _textToolEditingActor = actor;
        _textToolEditingCreatedActor = created;
        _textToolOriginalText = actor.State.CommentText;
        SelectActor(actor);
        _textToolEditorOverlay.Visible = true;
        _textToolHoverHint.Visible = false;
        _textToolEditField.Text = created ? TextToolPlaceholder : _textToolOriginalText;
        _textToolStylePicker.Disabled = true;
        _textToolSnapToggle.Disabled = true;
        _textToolRotateButton.Disabled = true;
        _textToolEditField.GrabFocus();
        if (created)
            _textToolEditField.SelectAll();
        if (_textToolPreview is not null && IsInstanceValid(_textToolPreview))
            _textToolPreview.Visible = false;
    }

    private void UpdateTextToolEditorText()
    {
        if (_textToolEditingActor is { } actor && IsInstanceValid(actor))
            actor.SetCommentText(_textToolEditField.Text);
    }

    private void HandleTextToolEditorInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        if (key.Keycode == Key.Escape)
        {
            _textToolEditField.AcceptEvent();
            CancelTextToolEditing();
        }
        else if (key.Keycode == Key.Enter && !key.ShiftPressed)
        {
            _textToolEditField.AcceptEvent();
            FinishTextToolEditing();
        }
    }

    private void FinishTextToolEditing()
    {
        WorldActorNode? actor = _textToolEditingActor;
        if (actor is null)
            return;
        string text = _textToolEditField.Text;
        bool created = _textToolEditingCreatedActor;
        string originalText = _textToolOriginalText;
        _textToolEditingActor = null;
        _textToolEditingCreatedActor = false;
        _textToolEditorOverlay.Visible = false;
        if (!IsInstanceValid(actor) || !_actors.Contains(actor))
            return;

        actor.SetCommentText(text);
        if (created && text == TextToolPlaceholder)
        {
            RemoveActorFromWorld(actor.LegacyName);
            _status.Text = "Empty text panel discarded.";
        }
        else if (created)
        {
            string actorName = actor.LegacyName;
            string displayName = actor.State.DisplayName;
            string actorJson = actor.State.Data.ToJsonString();
            int actorIndex = _actors.IndexOf(actor);
            SelectActor(actor);
            RecordHistory($"Create text panel {displayName}",
                () => RemoveActorFromWorld(actorName),
                () => RestoreActorFromJson(actorJson, actorIndex));
            _status.Text = "Text panel created.";
        }
        else
        {
            SelectActor(actor);
            if (originalText != text)
            {
                string actorName = actor.LegacyName;
                string displayName = actor.State.DisplayName;
                RecordHistory($"Edit text panel {displayName}",
                    () => SetActorStringProperty(actorName, "commentText", originalText),
                    () => SetActorStringProperty(actorName, "commentText", text));
                _status.Text = "Text panel updated.";
            }
        }

        _textToolStylePicker.Disabled = !_textToolActive;
        _textToolSnapToggle.Disabled = !_textToolActive;
        _textToolRotateButton.Disabled = !_textToolActive || _textToolBillboard;
        if (_textToolActive)
            UpdateTextToolPreview(GetViewport().GetMousePosition());
    }

    private void CancelTextToolEditing()
    {
        WorldActorNode? actor = _textToolEditingActor;
        if (actor is null)
            return;
        bool created = _textToolEditingCreatedActor;
        string originalText = _textToolOriginalText;
        _textToolEditingActor = null;
        _textToolEditingCreatedActor = false;
        _textToolEditorOverlay.Visible = false;
        if (IsInstanceValid(actor) && _actors.Contains(actor))
        {
            if (created)
                RemoveActorFromWorld(actor.LegacyName);
            else
            {
                actor.SetCommentText(originalText);
                SelectActor(actor);
            }
        }
        _status.Text = "Text editing canceled.";
        _textToolStylePicker.Disabled = !_textToolActive;
        _textToolSnapToggle.Disabled = !_textToolActive;
        _textToolRotateButton.Disabled = !_textToolActive || _textToolBillboard;
        if (_textToolActive)
            UpdateTextToolPreview(GetViewport().GetMousePosition());
    }

    private void RotateTextToolPanel()
    {
        if (!_textToolActive || _textToolBillboard || _textToolEditingActor is not null)
            return;
        _textToolManualQuarterTurns = (_textToolManualQuarterTurns + 1) % 4;
        UpdateTextToolPreview(GetViewport().GetMousePosition());
    }
}
