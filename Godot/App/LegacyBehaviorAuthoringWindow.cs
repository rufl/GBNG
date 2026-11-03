using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GBNG.Assets;
using GBNG.Behaviors;
using GBNG.Domain;
using GBNG.World;
using Godot;

namespace GBNG.App;

public sealed partial class LegacyBehaviorAuthoringWindow : Window
{
    private const string BuiltinBehaviorRoot = "res://Assets/StreamingAssets/BehaviorLibrary";
    private const string DefaultBrainId = "__DEFAULT_BRAIN__";
    private const string NewBehaviorSource = "// Broadcasts a readable message whenever this actor collides.\nexport function onCollision(msg) {\n  broadcast('ActorCollided', getDisplayName(msg.other));\n}\n";

    private readonly LegacySaveDocument _document;
    private readonly LegacyRenderableCatalog? _catalog;
    private readonly LegacyActorData _actor;
    private readonly Action _onChanged;
    private readonly ItemList _assignedUses = new();
    private readonly OptionButton _availableBehaviors = new();
    private readonly LegacyJavaScriptCodeEdit _scriptEditor = new();
    private readonly Label _scriptDiagnostics = new() { Name = "BehaviorScriptDiagnostics", AutowrapMode = TextServer.AutowrapMode.Word, Modulate = new Color(1f, 0.55f, 0.55f) };
    private readonly TextEdit _assignmentsEditor = new();
    private readonly Button _removeUseButton = new();
    private readonly Button _copyBehaviorButton = new();
    private readonly Button _applyScriptButton = new();
    private readonly Button _applyAssignmentsButton = new();
    private readonly Label _status = new();
    private readonly Label _availableBehaviorDetails = new() { AutowrapMode = TextServer.AutowrapMode.Word };
    private readonly Label _selectedBehaviorDetails = new() { AutowrapMode = TextServer.AutowrapMode.Word };
    private readonly VBoxContainer _metadataEditor = new();
    private readonly LineEdit _metadataTitle = new();
    private readonly LineEdit _metadataCategories = new();
    private readonly TextEdit _metadataDescription = new();
    private readonly LineEdit _metadataImagePath = new();
    private readonly TextureRect _metadataImagePreview = new();
    private readonly Button _applyCardMetadataButton = new();
    private readonly Dictionary<string, string> _builtinSources = new(StringComparer.Ordinal);
    private bool _refreshing;
    private string _selectedUseId = "";
    private string _selectedUri = "";
    private readonly Dictionary<string, double> _scriptScrollByUri = new(StringComparer.Ordinal);
    private readonly Func<string, JsonArray?> _getPropertyDefinitions;
    private readonly Func<string, string, string?> _validateBehaviorSource;
    private readonly ScrollContainer _propertiesScroll = new();
    private readonly VBoxContainer _propertiesContainer = new();
    private readonly CheckBox _rawAssignmentsMode = new();
    private readonly Dictionary<string, (string Type, Control Editor)> _propertyEditors = new(StringComparer.Ordinal);
    private JsonArray _propertyDefinitions = new();
    private bool _propertyDefinitionsAvailable;

    private sealed record BehaviorChoice(string Uri, string Label);
    private sealed record CardChoice(string UseId, string Label);

    private sealed partial class StringArrayEditor : VBoxContainer
    {
        private sealed record ValueRow(OptionButton Picker, HBoxContainer Container, Button MoveUp, Button MoveDown);

        private readonly Func<string, OptionButton> _pickerFactory;
        private readonly List<ValueRow> _rows = new();
        private readonly Button _addButton = new();
        private readonly int? _maximumCount;

        public StringArrayEditor(
            Func<string, OptionButton> pickerFactory,
            IEnumerable<string> values,
            string addButtonText = "Add value",
            int? maximumCount = null)
        {
            _pickerFactory = pickerFactory;
            _maximumCount = maximumCount;
            _addButton.Text = addButtonText;
            CustomMinimumSize = new Vector2(0f, 100f);
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            AddThemeConstantOverride("separation", 4);
            foreach (string value in values)
                AddValueRow(value);
            _addButton.Pressed += () => AddValueRow("");
            AddChild(_addButton);
            RefreshRowButtons();
        }

        public event Action? Changed;

        public JsonArray GetValues()
        {
            var values = new JsonArray();
            foreach (ValueRow row in _rows)
                values.Add(row.Picker.GetItemMetadata(row.Picker.Selected).AsString());
            return values;
        }

        private void AddValueRow(string value)
        {
            var container = new HBoxContainer();
            var picker = _pickerFactory(value);
            picker.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            picker.ItemSelected += _ => Changed?.Invoke();
            var moveUp = new Button { Text = "Up" };
            moveUp.Pressed += () => MoveValueRow(picker, -1);
            var moveDown = new Button { Text = "Down" };
            moveDown.Pressed += () => MoveValueRow(picker, 1);
            var removeButton = new Button { Text = "Remove" };
            removeButton.Pressed += () => RemoveValueRow(picker);
            container.AddChild(picker);
            container.AddChild(moveUp);
            container.AddChild(moveDown);
            container.AddChild(removeButton);
            _rows.Add(new ValueRow(picker, container, moveUp, moveDown));
            AddChild(container);
            if (_addButton.GetParent() == this)
                MoveChild(container, GetChildCount() - 2);
            RefreshRowButtons();
            Changed?.Invoke();
        }

        private void RemoveValueRow(OptionButton picker)
        {
            int index = _rows.FindIndex(row => row.Picker == picker);
            if (index < 0)
                return;
            ValueRow row = _rows[index];
            _rows.RemoveAt(index);
            RemoveChild(row.Container);
            row.Container.QueueFree();
            RefreshRowButtons();
            Changed?.Invoke();
        }

        private void MoveValueRow(OptionButton picker, int offset)
        {
            int index = _rows.FindIndex(row => row.Picker == picker);
            if (index < 0)
                return;
            int targetIndex = Math.Clamp(index + offset, 0, _rows.Count - 1);
            if (targetIndex == index)
                return;
            ValueRow row = _rows[index];
            _rows.RemoveAt(index);
            _rows.Insert(targetIndex, row);
            MoveChild(row.Container, targetIndex);
            RefreshRowButtons();
            Changed?.Invoke();
        }

        private void RefreshRowButtons()
        {
            for (int index = 0; index < _rows.Count; index++)
            {
                _rows[index].MoveUp.Disabled = index == 0;
                _rows[index].MoveDown.Disabled = index == _rows.Count - 1;
            }
            _addButton.Disabled = _maximumCount.HasValue && _rows.Count >= _maximumCount.Value;
        }
    }

    public LegacyBehaviorAuthoringWindow(
        LegacySaveDocument document,
        LegacyActorData actor,
        Action onChanged,
        Func<string, JsonArray?> getPropertyDefinitions,
        Func<string, string, string?> validateBehaviorSource)
        : this(document, actor, onChanged, getPropertyDefinitions, validateBehaviorSource, null)
    {
    }

    public LegacyBehaviorAuthoringWindow(
        LegacySaveDocument document,
        LegacyActorData actor,
        Action onChanged,
        Func<string, JsonArray?> getPropertyDefinitions,
        Func<string, string, string?> validateBehaviorSource,
        LegacyRenderableCatalog? catalog)
    {
        _document = document;
        _actor = actor;
        _onChanged = onChanged;
        _getPropertyDefinitions = getPropertyDefinitions;
        _validateBehaviorSource = validateBehaviorSource;
        _catalog = catalog;
        Title = $"Behavior editor — {actor.DisplayName}";
        Size = new Vector2I(1000, 700);
        CloseRequested += () => QueueFree();
        BuildInterface();
        RefreshAvailableBehaviors();
        RefreshAssignedUses();
    }

    public void RefreshFromDocument()
    {
        if (!_document.Actors.Contains(_actor))
        {
            QueueFree();
            return;
        }
        RefreshAvailableBehaviors();
        RefreshAssignedUses();
    }

    private void BuildInterface()
    {
        var margin = new MarginContainer { AnchorRight = 1f, AnchorBottom = 1f };
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        AddChild(margin);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 8);
        margin.AddChild(layout);
        layout.AddChild(new Label
        {
            Text = "Edit behavior uses and embedded JavaScript. Changes update the project in memory; use Save or Save as… in GBNG to persist them.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });

        var columns = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        layout.AddChild(columns);

        var left = new VBoxContainer { CustomMinimumSize = new Vector2(300f, 0f) };
        columns.AddChild(left);
        left.AddChild(new Label { Text = "Assigned behaviors" });
        _assignedUses.Name = "AssignedBehaviorUses";
        _assignedUses.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _assignedUses.ItemSelected += SelectAssignedUse;
        left.AddChild(_assignedUses);

        _availableBehaviors.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _availableBehaviors.ItemSelected += _ => RefreshAvailableBehaviorDetails();
        left.AddChild(_availableBehaviors);
        _availableBehaviorDetails.CustomMinimumSize = new Vector2(0f, 56f);
        left.AddChild(_availableBehaviorDetails);
        var addSelected = new Button { Text = "Add selected behavior" };
        addSelected.Pressed += AddSelectedBehavior;
        left.AddChild(addSelected);
        var createBehavior = new Button { Text = "Create custom behavior" };
        createBehavior.Pressed += CreateCustomBehavior;
        left.AddChild(createBehavior);
        _removeUseButton.Text = "Remove assigned behavior";
        _removeUseButton.Disabled = true;
        _removeUseButton.Pressed += RemoveSelectedUse;
        left.AddChild(_removeUseButton);
        var openCanvas = new Button { Text = "Open visual panel canvas" };
        openCanvas.Pressed += () =>
        {
            var canvas = new LegacyPanelCanvasWindow(_document, _actor, _onChanged, _getPropertyDefinitions, CreateCustomBehavior);
            AddChild(canvas);
            canvas.PopupCentered(new Vector2I(1100, 760));
        };
        left.AddChild(openCanvas);

        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        columns.AddChild(right);
        var scriptToolbar = new HBoxContainer();
        right.AddChild(_selectedBehaviorDetails);
        _metadataEditor.Name = "CardMetadataEditor";
        _metadataEditor.Visible = false;
        _metadataEditor.AddChild(new Label { Text = "Embedded card metadata" });
        _metadataEditor.AddChild(new Label { Text = "Title" });
        _metadataTitle.Name = "CardMetadataTitle";
        _metadataTitle.PlaceholderText = "Card title";
        _metadataEditor.AddChild(_metadataTitle);
        _metadataEditor.AddChild(new Label { Text = "Categories (comma-separated)" });
        _metadataCategories.Name = "CardMetadataCategories";
        _metadataCategories.PlaceholderText = "Custom";
        _metadataEditor.AddChild(_metadataCategories);
        _metadataEditor.AddChild(new Label { Text = "Image resource path" });
        _metadataImagePath.Name = "CardMetadataImagePath";
        _metadataImagePath.PlaceholderText = "BuiltinAssets/CardImages/placeholder";
        _metadataImagePath.TextChanged += UpdateCardImagePreview;
        _metadataEditor.AddChild(_metadataImagePath);
        _metadataImagePreview.Name = "CardMetadataImagePreview";
        _metadataImagePreview.CustomMinimumSize = new Vector2(0f, 72f);
        _metadataImagePreview.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _metadataImagePreview.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        _metadataImagePreview.Visible = false;
        _metadataEditor.AddChild(_metadataImagePreview);
        _metadataEditor.AddChild(new Label { Text = "Description" });
        _metadataDescription.Name = "CardMetadataDescription";
        _metadataDescription.CustomMinimumSize = new Vector2(0f, 56f);
        _metadataDescription.PlaceholderText = "Describe what this card does.";
        _metadataEditor.AddChild(_metadataDescription);
        _applyCardMetadataButton.Name = "ApplyCardMetadata";
        _applyCardMetadataButton.Text = "Apply card metadata";
        _applyCardMetadataButton.Disabled = true;
        _applyCardMetadataButton.Pressed += ApplyCardMetadata;
        _metadataEditor.AddChild(_applyCardMetadataButton);
        right.AddChild(_metadataEditor);
        scriptToolbar.AddChild(new Label { Text = "JavaScript · Ctrl+Space for API suggestions", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var zoomOut = new Button { Name = "BehaviorCodeZoomOut", Text = "A−", TooltipText = "Reduce JavaScript text size" };
        zoomOut.Pressed += _scriptEditor.ZoomOut;
        scriptToolbar.AddChild(zoomOut);
        var zoomIn = new Button { Name = "BehaviorCodeZoomIn", Text = "A+", TooltipText = "Increase JavaScript text size" };
        zoomIn.Pressed += _scriptEditor.ZoomIn;
        scriptToolbar.AddChild(zoomIn);
        var zoomReset = new Button { Name = "BehaviorCodeZoomReset", Text = "Reset zoom", TooltipText = "Restore default JavaScript text size" };
        zoomReset.Pressed += _scriptEditor.ResetZoom;
        scriptToolbar.AddChild(zoomReset);
        _copyBehaviorButton.Text = "Create editable copy";
        _copyBehaviorButton.Disabled = true;
        _copyBehaviorButton.Pressed += CopySelectedBehavior;
        scriptToolbar.AddChild(_copyBehaviorButton);
        _applyScriptButton.Text = "Apply code";
        _applyScriptButton.Disabled = true;
        _applyScriptButton.Pressed += ApplyScript;
        scriptToolbar.AddChild(_applyScriptButton);
        right.AddChild(scriptToolbar);
        _scriptDiagnostics.Visible = false;
        _scriptDiagnostics.CustomMinimumSize = new Vector2(0f, 24f);
        right.AddChild(_scriptDiagnostics);
        _scriptEditor.Name = "BehaviorJavaScript";
        _scriptEditor.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _scriptEditor.CustomMinimumSize = new Vector2(0f, 250f);
        _scriptEditor.Editable = false;
        _scriptEditor.TextChanged += ClearScriptDiagnostic;
        right.AddChild(_scriptEditor);

        var propertiesToolbar = new HBoxContainer();
        propertiesToolbar.AddChild(new Label { Text = "Structured properties", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _rawAssignmentsMode.Text = "Edit raw JSON";
        _rawAssignmentsMode.Toggled += SetRawAssignmentsMode;
        propertiesToolbar.AddChild(_rawAssignmentsMode);
        _applyAssignmentsButton.Text = "Apply properties";
        _applyAssignmentsButton.Disabled = true;
        _applyAssignmentsButton.Pressed += ApplyAssignments;
        propertiesToolbar.AddChild(_applyAssignmentsButton);
        right.AddChild(propertiesToolbar);

        _propertiesScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _propertiesScroll.CustomMinimumSize = new Vector2(0f, 130f);
        _propertiesContainer.AddThemeConstantOverride("separation", 8);
        _propertiesScroll.AddChild(_propertiesContainer);
        right.AddChild(_propertiesScroll);

        _assignmentsEditor.CustomMinimumSize = new Vector2(0f, 180f);
        _assignmentsEditor.Editable = false;
        _assignmentsEditor.Visible = false;
        right.AddChild(_assignmentsEditor);

        _status.AutowrapMode = TextServer.AutowrapMode.Word;
        layout.AddChild(_status);
    }

    private void RefreshAvailableBehaviors()
    {
        string previousUri = _availableBehaviors.Selected >= 0
            ? _availableBehaviors.GetItemMetadata(_availableBehaviors.Selected).AsString()
            : "";
        _availableBehaviors.Clear();
        _builtinSources.Clear();
        var choices = new List<BehaviorChoice>();

        JsonObject? database = _document.BehaviorDatabase;
        if (TryGetPairedArrays(database, "behaviorIds", "behaviors", out JsonArray behaviorIds, out JsonArray behaviors))
        {
            int count = Math.Min(behaviorIds.Count, behaviors.Count);
            for (int index = 0; index < count; index++)
            {
                string id = LegacySaveDocument.ReadString(behaviorIds[index]);
                if (id.Length == 0 || behaviors[index] is not JsonObject behavior)
                    continue;
                choices.Add(new BehaviorChoice($"embedded:{id}", GetEmbeddedLabel(id, behavior)));
            }
        }

        var builtinFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string path in LegacyBehaviorRuntime.EnumerateFiles(BuiltinBehaviorRoot, ".js.txt"))
        {
            if (path.Contains("/Specs/", StringComparison.Ordinal))
                continue;
            string name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
            string uri = $"builtin:{name}";
            if (!builtinFiles.TryGetValue(uri, out List<string>? paths))
                builtinFiles.Add(uri, paths = new List<string>());
            paths.Add(path);
        }
        foreach ((string uri, List<string> paths) in builtinFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (paths.Count != 1)
                continue;
            _builtinSources.Add(uri, paths[0]);
            choices.Add(new BehaviorChoice(uri, Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(paths[0]))));
        }

        foreach (BehaviorChoice choice in choices.OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(choice => choice.Uri, StringComparer.Ordinal))
        {
            _availableBehaviors.AddItem(choice.Label);
            _availableBehaviors.SetItemMetadata(_availableBehaviors.ItemCount - 1, choice.Uri);
            if (choice.Uri == previousUri)
                _availableBehaviors.Select(_availableBehaviors.ItemCount - 1);
        }
        if (_availableBehaviors.ItemCount > 0 && _availableBehaviors.Selected < 0)
            _availableBehaviors.Select(0);
        _availableBehaviors.Disabled = _availableBehaviors.ItemCount == 0;
        RefreshAvailableBehaviorDetails();
    }
    private void RefreshAvailableBehaviorDetails()
    {
        _availableBehaviorDetails.Text = _availableBehaviors.Selected < 0
            ? "No behavior library entries are available."
            : FormatBehaviorDetails(_availableBehaviors.GetItemMetadata(_availableBehaviors.Selected).AsString());
    }

    private void RefreshAssignedUses()
    {
        string previousUseId = _selectedUseId;
        _refreshing = true;
        _assignedUses.Clear();
        JsonObject? brain = GetCurrentBrain();
        if (brain?["behaviorUses"] is JsonArray uses)
        {
            for (int index = 0; index < uses.Count; index++)
            {
                if (uses[index] is not JsonObject use)
                    continue;
                string useId = LegacySaveDocument.ReadString(use["id"]);
                string uri = LegacySaveDocument.ReadString(use["behaviorUri"]);
                if (useId.Length == 0)
                    continue;
                _assignedUses.AddItem(GetBehaviorLabel(uri));
                _assignedUses.SetItemMetadata(_assignedUses.ItemCount - 1, useId);
                if (useId == previousUseId)
                    _assignedUses.Select(_assignedUses.ItemCount - 1);
            }
        }
        if (_assignedUses.ItemCount > 0 && _assignedUses.GetSelectedItems().Length == 0)
            _assignedUses.Select(0);
        _refreshing = false;
        _selectedUseId = _assignedUses.GetSelectedItems().Length > 0
            ? _assignedUses.GetItemMetadata(_assignedUses.GetSelectedItems()[0]).AsString()
            : "";
        LoadSelectedUse();
    }

    private void SelectAssignedUse(long index)
    {
        if (_refreshing)
            return;
        _selectedUseId = _assignedUses.GetItemMetadata((int)index).AsString();
        LoadSelectedUse();
    }

    private void LoadSelectedUse()
    {
        ClearScriptDiagnostic();
        if (_selectedUri.Length > 0)
            _scriptScrollByUri[_selectedUri] = _scriptEditor.ScrollVertical;
        JsonObject? use = GetSelectedUse();
        _selectedUri = use is null ? "" : LegacySaveDocument.ReadString(use["behaviorUri"]);
        JsonObject? embeddedBehavior = _selectedUri.StartsWith("embedded:", StringComparison.Ordinal)
            ? FindEmbeddedBehavior(_selectedUri["embedded:".Length..])
            : null;
        _selectedBehaviorDetails.Text = use is null
            ? "Select an assigned behavior to inspect its card details."
            : FormatBehaviorDetails(_selectedUri);
        bool canEditCardMetadata = embeddedBehavior is not null && RefreshCardMetadataEditor(embeddedBehavior);
        _metadataEditor.Visible = canEditCardMetadata;
        _applyCardMetadataButton.Disabled = !canEditCardMetadata;
        if (!canEditCardMetadata)
        {
            _metadataTitle.Text = "";
            _metadataCategories.Text = "";
            _metadataCategories.Visible = true;
            _metadataDescription.Text = "";
            _metadataImagePath.Text = "";
            UpdateCardImagePreview("");
        }
        bool hasSource = embeddedBehavior is not null
            || (_selectedUri.StartsWith("builtin:", StringComparison.Ordinal) && _builtinSources.ContainsKey(_selectedUri));
        _copyBehaviorButton.Disabled = use is null || !hasSource;
        _applyAssignmentsButton.Disabled = use is null;
        _rawAssignmentsMode.Disabled = use is null;
        _assignmentsEditor.Editable = use is not null;
        _assignmentsEditor.Text = use?["propertyAssignments"] is JsonArray assignments
            ? assignments.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            : "[]";
        _assignmentsEditor.Visible = _rawAssignmentsMode.ButtonPressed;
        _propertiesScroll.Visible = !_rawAssignmentsMode.ButtonPressed;
        _applyAssignmentsButton.Text = _rawAssignmentsMode.ButtonPressed ? "Apply raw JSON" : "Apply properties";
        BuildPropertyEditors(use);
        _applyAssignmentsButton.Disabled = use is null || (!_rawAssignmentsMode.ButtonPressed && !_propertyDefinitionsAvailable);

        string source = "";
        bool editable = false;
        if (embeddedBehavior is not null)
        {
            source = LegacySaveDocument.ReadString(embeddedBehavior["javascript"]);
            editable = true;
        }
        else if (_selectedUri.StartsWith("builtin:", StringComparison.Ordinal)
                 && _builtinSources.TryGetValue(_selectedUri, out string? path))
        {
            source = Godot.FileAccess.GetFileAsString(path);
        }
        _scriptEditor.Text = source;
        _scriptEditor.ScrollVertical = _scriptScrollByUri.TryGetValue(_selectedUri, out double scrollVertical)
            ? scrollVertical
            : 0d;
        _scriptEditor.Editable = editable;
        _applyScriptButton.Disabled = !editable;
        _status.Text = use is null
            ? "Select a behavior use, add one from the library, or create a custom behavior."
            : editable
                ? "Embedded behavior source is editable. Apply code to validate it in the Godot runtime."
                : source.Length > 0
                    ? "Built-in behavior source is read-only; create an editable copy to modify this use."
                    : $"Source for '{_selectedUri}' is not available in this project.";
    }

    private void AddSelectedBehavior()
    {
        if (_availableBehaviors.Selected < 0)
            return;
        string uri = _availableBehaviors.GetItemMetadata(_availableBehaviors.Selected).AsString();
        if (uri.Length == 0)
            return;
        JsonArray uses = EnsureBehaviorUses(EnsureWritableBrain());
        string useId = NewId();
        uses.Add(CreateBehaviorUse(useId, uri));
        _selectedUseId = useId;
        NotifyChanged("Added behavior use.");
        RefreshAvailableBehaviors();
        RefreshAssignedUses();
    }

    private void CreateCustomBehavior()
    {
        string behaviorId = NewId();
        AddEmbeddedBehavior(behaviorId, "Custom", NewBehaviorSource, CreateDefaultMetadata("Custom behavior"));
        JsonArray uses = EnsureBehaviorUses(EnsureWritableBrain());
        string useId = NewId();
        uses.Add(CreateBehaviorUse(useId, $"embedded:{behaviorId}"));
        _selectedUseId = useId;
        NotifyChanged("Created and assigned a custom behavior.");
        RefreshAvailableBehaviors();
        RefreshAssignedUses();
    }

    private bool RefreshCardMetadataEditor(JsonObject behavior)
    {
        string metadataJson = LegacySaveDocument.ReadString(behavior["metadataJson"]);
        JsonObject? metadata;
        if (metadataJson.Length == 0)
        {
            metadata = new JsonObject();
        }
        else
        {
            try
            {
                metadata = JsonNode.Parse(metadataJson) as JsonObject;
            }
            catch (JsonException)
            {
                return false;
            }
        }
        if (metadata is null)
            return false;
        if (metadata["cardSystemPanelData"] is JsonObject panelData && LegacySaveDocument.ReadBool(panelData["isPanel"]))
            return false;

        JsonObject? cardData = metadata["cardSystemCardData"] as JsonObject;
        _metadataTitle.Text = LegacySaveDocument.ReadString(cardData?["title"], LegacySaveDocument.ReadString(behavior["label"]));
        _metadataCategories.Text = cardData?["categories"] is JsonArray categories
            ? string.Join(", ", categories.Select(node => LegacySaveDocument.ReadString(node)).Where(value => value.Length > 0))
            : "";
        _metadataCategories.Visible = cardData is null || LegacySaveDocument.ReadBool(cardData["isCard"], true);
        _metadataDescription.Text = LegacySaveDocument.ReadString(cardData?["description"]);
        _metadataImagePath.Text = LegacySaveDocument.ReadString(cardData?["imageResourcePath"]);
        UpdateCardImagePreview(_metadataImagePath.Text);
        return true;
    }
    private void UpdateCardImagePreview(string imageResourcePath)
    {
        _metadataImagePreview.Texture = LegacyBehaviorImages.LoadCardIcon(imageResourcePath);
        _metadataImagePreview.Visible = _metadataImagePreview.Texture is not null;
    }

    private void ApplyCardMetadata()
    {
        if (!_selectedUri.StartsWith("embedded:", StringComparison.Ordinal)
            || FindEmbeddedBehavior(_selectedUri["embedded:".Length..]) is not JsonObject behavior)
            return;

        string metadataJson = LegacySaveDocument.ReadString(behavior["metadataJson"]);
        JsonObject metadata;
        if (metadataJson.Length == 0)
        {
            metadata = new JsonObject();
        }
        else
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(metadataJson);
            }
            catch (JsonException)
            {
                _status.Text = "Card metadata is invalid JSON and was not changed.";
                return;
            }
            if (parsed is not JsonObject parsedMetadata)
            {
                _status.Text = "Card metadata must be a JSON object and was not changed.";
                return;
            }
            metadata = parsedMetadata;
        }

        if (metadata["cardSystemPanelData"] is JsonObject panelData && LegacySaveDocument.ReadBool(panelData["isPanel"]))
        {
            _status.Text = "Panel metadata cannot be changed in the card metadata editor.";
            return;
        }
        if (metadata["cardSystemCardData"] is not JsonObject cardData)
        {
            cardData = new JsonObject { ["isCard"] = true };
            metadata["cardSystemCardData"] = cardData;
        }
        cardData["title"] = _metadataTitle.Text;
        cardData["description"] = _metadataDescription.Text;
        cardData["imageResourcePath"] = _metadataImagePath.Text;
        if (LegacySaveDocument.ReadBool(cardData["isCard"], true))
        {
            var categories = new JsonArray();
            var seenCategories = new HashSet<string>(StringComparer.Ordinal);
            foreach (string category in _metadataCategories.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (seenCategories.Add(category))
                    categories.Add(category);
            }
            cardData["categories"] = categories;
        }
        behavior["metadataJson"] = metadata.ToJsonString();
        NotifyChanged("Updated custom card metadata. Save the project to persist it.");
        RefreshAvailableBehaviors();
        RefreshAssignedUses();
    }

    private void RemoveSelectedUse()
    {
        JsonObject? brain = EnsureWritableBrain();
        if (brain["behaviorUses"] is not JsonArray uses)
            return;
        int index = -1;
        for (int candidate = 0; candidate < uses.Count; candidate++)
        {
            if (uses[candidate] is JsonObject use && LegacySaveDocument.ReadString(use["id"]) == _selectedUseId)
            {
                index = candidate;
                break;
            }
        }
        if (index < 0)
            return;
        uses.RemoveAt(index);
        _selectedUseId = "";
        NotifyChanged("Removed behavior use.");
        RefreshAssignedUses();
    }

    private void CopySelectedBehavior()
    {
        JsonObject? use = GetSelectedUse();
        if (use is null || _selectedUri.Length == 0)
            return;
        string label = GetBehaviorLabel(_selectedUri);
        string metadataJson = GetSelectedMetadataJson();
        string behaviorId = NewId();
        AddEmbeddedBehavior(behaviorId, label, _scriptEditor.Text, metadataJson);
        JsonObject writableUse = FindSelectedUse(EnsureWritableBrain())
            ?? throw new InvalidDataException("The selected behavior use no longer exists.");
        writableUse["behaviorUri"] = $"embedded:{behaviorId}";
        NotifyChanged("Created an editable embedded copy.");
        RefreshAvailableBehaviors();
        RefreshAssignedUses();
    }

    private void ApplyScript()
    {
        if (!_selectedUri.StartsWith("embedded:", StringComparison.Ordinal))
            return;
        JsonObject? behavior = FindEmbeddedBehavior(_selectedUri["embedded:".Length..]);
        if (behavior is null)
        {
            _status.Text = "The embedded behavior no longer exists.";
            return;
        }
        string source = _scriptEditor.Text;
        string? diagnostic = _validateBehaviorSource(_selectedUri, source);
        _scriptEditor.ClearDiagnosticLines();
        if (diagnostic is not null)
        {
            _scriptDiagnostics.Text = diagnostic;
            int diagnosticLine = ParseDiagnosticLine(diagnostic);
            if (diagnosticLine > 0)
                _scriptEditor.AddDiagnosticLine(diagnosticLine, diagnostic);
            _scriptDiagnostics.Visible = true;
            _status.Text = "JavaScript syntax error. Fix the reported line before applying.";
            return;
        }

        ClearScriptDiagnostic();
        behavior["javascript"] = source;
        behavior["draftJavascript"] = "";
        NotifyChanged("Applied JavaScript. Save the project to persist it.");
        RefreshAvailableBehaviors();
        RefreshAssignedUses();
    }

    private void ClearScriptDiagnostic()
    {
        _scriptDiagnostics.Text = "";
        _scriptEditor.ClearDiagnosticLines();
        _scriptDiagnostics.Visible = false;
    }
    public void AddRuntimeDiagnostic(int lineNumber, string message)
    {
        string diagnostic = lineNumber > 0 ? $"Line {lineNumber}: {message}" : message;
        _scriptDiagnostics.Text = _scriptDiagnostics.Text.Length == 0
            ? diagnostic
            : $"{_scriptDiagnostics.Text}\n{diagnostic}";
        _scriptDiagnostics.Visible = diagnostic.Length > 0;
        if (lineNumber > 0)
            _scriptEditor.AddDiagnosticLine(lineNumber, diagnostic);
    }
    private static int ParseDiagnosticLine(string diagnostic)
    {
        const string prefix = "Line ";
        if (!diagnostic.StartsWith(prefix, StringComparison.Ordinal))
            return 0;
        int end = diagnostic.IndexOf(':', prefix.Length);
        return end > prefix.Length
               && int.TryParse(diagnostic.AsSpan(prefix.Length, end - prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int line)
               && line > 0
            ? line
            : 0;
    }

    private void SetRawAssignmentsMode(bool raw)
    {
        _assignmentsEditor.Visible = raw;
        _propertiesScroll.Visible = !raw;
        _applyAssignmentsButton.Text = raw ? "Apply raw JSON" : "Apply properties";
        _applyAssignmentsButton.Disabled = GetSelectedUse() is null || (!raw && !_propertyDefinitionsAvailable);
    }

    private void BuildPropertyEditors(JsonObject? use)
    {
        foreach (Node child in _propertiesContainer.GetChildren())
        {
            _propertiesContainer.RemoveChild(child);
            child.QueueFree();
        }
        _propertyEditors.Clear();
        _propertyDefinitions = new JsonArray();
        _propertyDefinitionsAvailable = false;
        if (use is null)
        {
            _propertiesContainer.AddChild(new Label { Text = "Select an assigned behavior to edit its properties." });
            return;
        }

        try
        {
            JsonArray? definitions = _getPropertyDefinitions(_selectedUri);
            if (definitions is null)
            {
                _propertiesContainer.AddChild(new Label { Text = "Property definitions are unavailable. Use raw JSON to inspect or repair assignments." });
                return;
            }
            _propertyDefinitions = definitions;
            _propertyDefinitionsAvailable = true;
            Dictionary<string, JsonNode?> assignedValues = ReadAssignedValues(use);
            foreach (JsonNode? node in definitions)
            {
                if (node is not JsonObject definition)
                    continue;
                string propertyName = LegacySaveDocument.ReadString(definition["variableName"]);
                string type = LegacySaveDocument.ReadString(definition["type"]);
                if (propertyName.Length == 0 || type.Length == 0)
                    continue;
                JsonNode? value = assignedValues.TryGetValue(propertyName, out JsonNode? assignedValue)
                    ? assignedValue?.DeepClone()
                    : GetDefaultValue(definition, type);
                Control editor = CreatePropertyEditor(definition, type, value);
                _propertyEditors[propertyName] = (type, editor);
                ConnectPropertyEditor(editor);
                string label = LegacySaveDocument.ReadString(definition["label"], propertyName);
                string comment = LegacySaveDocument.ReadString(definition["comment"]);
                if (editor is CheckBox checkBox)
                {
                    checkBox.Text = label;
                    checkBox.TooltipText = comment;
                    _propertiesContainer.AddChild(checkBox);
                }
                else
                {
                    var row = new VBoxContainer();
                    row.AddChild(new Label { Text = label, TooltipText = comment });
                    row.AddChild(editor);
                    _propertiesContainer.AddChild(row);
                }
            }
            RefreshPropertyVisibility();
            if (definitions.Count == 0)
                _propertiesContainer.AddChild(new Label { Text = "This behavior has no editable properties." });
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or OverflowException)
        {
            _propertyDefinitionsAvailable = false;
            _propertiesContainer.AddChild(new Label { Text = $"Could not load behavior properties: {exception.Message}", AutowrapMode = TextServer.AutowrapMode.Word });
        }
    }

    private static Dictionary<string, JsonNode?> ReadAssignedValues(JsonObject use)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (use["propertyAssignments"] is not JsonArray assignments)
            return values;
        foreach (JsonNode? node in assignments)
        {
            if (node is not JsonObject assignment)
                continue;
            string name = LegacySaveDocument.ReadString(assignment["propertyName"]);
            string encoded = LegacySaveDocument.ReadString(assignment["valueJson"]);
            if (name.Length == 0 || encoded.Length == 0)
                continue;
            JsonNode? parsed = JsonNode.Parse(encoded);
            JsonNode? value = parsed is JsonObject envelope && envelope.ContainsKey("value")
                ? envelope["value"]?.DeepClone()
                : parsed?.DeepClone();
            values[name] = value;
        }
        return values;
    }

    private static JsonNode? GetDefaultValue(JsonObject definition, string type)
    {
        string raw = LegacySaveDocument.ReadString(definition["defaultValueString"]);
        switch (type)
        {
            case "Number":
                return JsonValue.Create(int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : 0);
            case "Decimal":
                return JsonValue.Create(double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double decimalValue) ? decimalValue : 1d);
            case "Boolean":
                return JsonValue.Create(bool.TryParse(raw, out bool boolean) && boolean);
            case "NumberArray":
            case "StringArray":
            case "EnumArray":
            case "ActorArray":
                return raw.Length == 0 ? new JsonArray() : JsonNode.Parse(raw) as JsonArray ?? new JsonArray();
            case "CardDeck":
                return new JsonArray();
            case "Color":
                return JsonValue.Create(raw.Length == 0 ? "#ffffff" : raw);
            default:
                return JsonValue.Create(raw);
        }
    }

    private Control CreatePropertyEditor(JsonObject definition, string type, JsonNode? value)
    {
        string stringValue = ReadPropertyString(value);
        switch (type)
        {
            case "Boolean":
                return new CheckBox { ButtonPressed = ReadPropertyBoolean(value) };
            case "Number":
            case "Decimal":
                return new SpinBox
                {
                    MinValue = type == "Number" ? int.MinValue : -1_000_000_000d,
                    MaxValue = type == "Number" ? int.MaxValue : 1_000_000_000d,
                    Step = type == "Number" ? 1d : 0.01d,
                    Value = ReadPropertyNumber(value)
                };
            case "Enum":
                if (definition["allowedValues"] is JsonArray allowedValues && allowedValues.Count > 0)
                {
                    var choices = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                    bool found = false;
                    foreach (JsonNode? allowedValue in allowedValues)
                    {
                        if (allowedValue is not JsonObject option)
                            continue;
                        string optionValue = LegacySaveDocument.ReadString(option["value"]);
                        string optionLabel = LegacySaveDocument.ReadString(option["label"], optionValue);
                        choices.AddItem(optionLabel);
                        choices.SetItemMetadata(choices.ItemCount - 1, optionValue);
                        if (optionValue == stringValue)
                        {
                            choices.Select(choices.ItemCount - 1);
                            found = true;
                        }
                    }
                    if (!found && stringValue.Length > 0)
                    {
                        choices.AddItem($"Unknown · {stringValue}");
                        choices.SetItemMetadata(choices.ItemCount - 1, stringValue);
                        choices.Select(choices.ItemCount - 1);
                    }
                    return choices;
                }
                return new LineEdit { Text = stringValue, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            case "Color":
                var picker = new ColorPickerButton { EditAlpha = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                try
                {
                    picker.Color = Color.FromHtml(stringValue);
                }
                catch (ArgumentException)
                {
                    picker.Color = Colors.White;
                }
                return picker;
            case "Sound":
                return CreateSojoPicker("SoundEffect", stringValue);
            case "ParticleEffect":
                return CreateSojoPicker("ParticleEffect", stringValue);
            case "Image":
                return CreateImagePicker(stringValue);
            case "Prefab":
                return CreateSojoPicker("ActorPrefab", stringValue);
            case "CardDeck":
                return CreateCardDeckEditor(definition, value);
            case "Actor":
                return CreateActorEditor(definition, stringValue);
            case "ActorGroup":
                return CreateActorGroupEditor(definition, stringValue);
            case "ActorArray":
                return CreateActorArrayEditor(definition, value);
            case "EnumArray":
                if (definition["allowedValues"] is JsonArray enumValues && enumValues.Count > 0)
                    return CreateEnumArrayEditor(definition, value);
                return new TextEdit
                {
                    Text = value is JsonArray enumArray ? enumArray.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : "[]",
                    CustomMinimumSize = new Vector2(0f, 70f),
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
            case "NumberArray":
            case "StringArray":
                return new TextEdit
                {
                    Text = value is JsonArray array ? array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : "[]",
                    CustomMinimumSize = new Vector2(0f, 70f),
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
            default:
                return new LineEdit { Text = stringValue, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        }
    }

    private OptionButton CreateActorEditor(JsonObject definition, string selectedValue)
    {
        var picker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        AddPickerOption(picker, "(none)", "", selectedValue);
        foreach (LegacyActorData actor in GetSelectableActors(definition))
            AddPickerOption(picker, $"{actor.DisplayName} · {actor.Name}", actor.Name, selectedValue);
        AddUnknownPickerOption(picker, selectedValue);
        return picker;
    }

    private OptionButton CreateActorGroupEditor(JsonObject definition, string selectedValue)
    {
        var picker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        AddPickerOption(picker, "(no actors)", "", selectedValue);
        AddPickerOption(picker, "All actors", "@ANY", selectedValue);

        IEnumerable<LegacyActorData> actors = GetSelectableActors(definition);
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (LegacyActorData actor in actors)
        {
            if (actor.Data["tags"] is not JsonArray actorTags)
                continue;
            foreach (JsonNode? tagNode in actorTags)
            {
                string tag = LegacySaveDocument.ReadString(tagNode);
                if (tag.Length > 0)
                    tags.Add(tag);
            }
        }
        foreach (string tag in tags)
            AddPickerOption(picker, $"Actors tagged · {tag}", $"@TAG:{tag}", selectedValue);
        foreach (LegacyActorData actor in actors)
            AddPickerOption(picker, $"Actor · {actor.DisplayName} · {actor.Name}", actor.Name, selectedValue);

        AddUnknownPickerOption(picker, selectedValue);
        return picker;
    }

    private OptionButton CreateImagePicker(string selectedId)
    {
        var picker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        AddPickerOption(picker, "(none)", "", selectedId);
        var addedValues = new HashSet<string>(StringComparer.Ordinal);
        if (_catalog is not null)
        {
            foreach (RenderableEntry entry in _catalog.Entries.OrderBy(item => item.Uri, StringComparer.OrdinalIgnoreCase))
            {
                foreach (string imagePath in entry.ImagePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    string value = entry.Uri.StartsWith("custom:", StringComparison.Ordinal) ? entry.Uri : imagePath;
                    if (!addedValues.Add(value))
                        continue;
                    string source = entry.Uri.StartsWith("custom:", StringComparison.Ordinal) ? "Imported" : "Built-in";
                    AddPickerOption(picker, $"{Path.GetFileNameWithoutExtension(imagePath)} · {source}", value, selectedId);
                }
            }
        }
        AddUnknownPickerOption(picker, selectedId);
        return picker;
    }

    private OptionButton CreateSojoPicker(string contentType, string selectedId)
    {
        var picker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        AddPickerOption(picker, "(none)", "", selectedId);
        if (_document.Root["sojoDatabase"] is JsonObject database && database["sojos"] is JsonArray sojos)
        {
            foreach (JsonNode? node in sojos)
            {
                if (node is not JsonObject sojo
                    || LegacySaveDocument.ReadString(sojo["contentType"]) != contentType)
                    continue;
                string id = LegacySaveDocument.ReadString(sojo["id"]);
                if (id.Length == 0)
                    continue;
                string label = LegacySaveDocument.ReadString(sojo["name"], id);
                AddPickerOption(picker, label, id, selectedId);
            }
        }
        AddUnknownPickerOption(picker, selectedId);
        return picker;
    }

    private StringArrayEditor CreateActorArrayEditor(JsonObject definition, JsonNode? value)
        => new(selected => CreateActorEditor(definition, selected), ReadStringArray(value));

    private StringArrayEditor CreateEnumArrayEditor(JsonObject definition, JsonNode? value)
    {
        JsonArray allowedValues = definition["allowedValues"] as JsonArray ?? new JsonArray();
        return new StringArrayEditor(selected => CreateEnumArrayOption(allowedValues, selected), ReadStringArray(value));
    }

    private static OptionButton CreateEnumArrayOption(JsonArray allowedValues, string selectedValue)
    {
        var picker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        bool hasEmptyOption = allowedValues
            .OfType<JsonObject>()
            .Any(option => LegacySaveDocument.ReadString(option["value"]) == "");
        if (!hasEmptyOption)
            AddPickerOption(picker, "(none)", "", selectedValue);
        foreach (JsonNode? optionNode in allowedValues)
        {
            if (optionNode is not JsonObject option)
                continue;
            string optionValue = LegacySaveDocument.ReadString(option["value"]);
            string optionLabel = LegacySaveDocument.ReadString(option["label"], optionValue);
            AddPickerOption(picker, optionLabel, optionValue, selectedValue);
        }
        AddUnknownPickerOption(picker, selectedValue);
        return picker;
    }

    private IEnumerable<LegacyActorData> GetSelectableActors(JsonObject definition)
    {
        bool allowOffstage = LegacySaveDocument.ReadBool(definition["allowOffstageActors"]);
        return _document.Actors.Where(actor => allowOffstage || !actor.IsOffstage);
    }

    private static List<string> ReadStringArray(JsonNode? value)
    {
        if (value is null)
            return new List<string>();
        if (value is not JsonArray values)
            throw new InvalidDataException("Expected a string array for a behavior property.");

        var result = new List<string>(values.Count);
        foreach (JsonNode? item in values)
        {
            if (item is not JsonValue jsonValue || !jsonValue.TryGetValue(out string? text) || text is null)
                throw new InvalidDataException("Every value in a behavior string array must be a string.");
            result.Add(text);
        }
        return result;
    }

    private static void AddPickerOption(OptionButton picker, string label, string value, string selectedValue)
    {
        picker.AddItem(label);
        int index = picker.ItemCount - 1;
        picker.SetItemMetadata(index, value);
        if (value == selectedValue)
            picker.Select(index);
    }

    private static void AddUnknownPickerOption(OptionButton picker, string selectedValue)
    {
        for (int index = 0; index < picker.ItemCount; index++)
        {
            if (picker.GetItemMetadata(index).AsString() == selectedValue)
                return;
        }
        if (selectedValue.Length > 0)
            AddPickerOption(picker, $"Unknown · {selectedValue}", selectedValue, selectedValue);
    }


    private StringArrayEditor CreateCardDeckEditor(JsonObject definition, JsonNode? value)
    {
        JsonObject? deckOptions = definition["deckOptions"] as JsonObject;
        string category = LegacySaveDocument.ReadString(deckOptions?["cardCategory"], "Custom");
        if (category.Length == 0)
            category = "Custom";

        var cards = new List<CardChoice>();
        if (GetCurrentBrain()?["behaviorUses"] is JsonArray uses)
        {
            foreach (JsonNode? node in uses)
            {
                if (node is not JsonObject use)
                    continue;
                string useId = LegacySaveDocument.ReadString(use["id"]);
                string uri = LegacySaveDocument.ReadString(use["behaviorUri"]);
                if (useId.Length > 0 && IsCardInCategory(uri, category))
                    cards.Add(new CardChoice(useId, GetBehaviorLabel(uri)));
            }
        }

        int? maximumCount = LegacySaveDocument.ReadBool(deckOptions?["oneCardOnly"]) ? 1 : null;
        return new StringArrayEditor(
            selectedId => CreateCardUsePicker(cards, selectedId),
            ReadStringArray(value),
            "Add card",
            maximumCount);
    }

    private static OptionButton CreateCardUsePicker(IReadOnlyList<CardChoice> cards, string selectedId)
    {
        var picker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        AddPickerOption(picker, "(choose a card)", "", selectedId);
        foreach (CardChoice card in cards)
            AddPickerOption(picker, card.Label, card.UseId, selectedId);
        AddUnknownPickerOption(picker, selectedId);
        return picker;
    }

    private bool IsCardInCategory(string uri, string category)
    {
        string metadataJson = "";
        if (uri.StartsWith("embedded:", StringComparison.Ordinal))
        {
            string id = uri["embedded:".Length..];
            metadataJson = LegacySaveDocument.ReadString(FindEmbeddedBehavior(id)?["metadataJson"]);
        }
        else if (uri.StartsWith("builtin:", StringComparison.Ordinal)
                 && _builtinSources.TryGetValue(uri, out string? path))
        {
            string metadataPath = path + ".metaJson";
            if (Godot.FileAccess.FileExists(metadataPath))
                metadataJson = Godot.FileAccess.GetFileAsString(metadataPath);
        }
        if (metadataJson.Length == 0)
            return uri.StartsWith("embedded:", StringComparison.Ordinal) && category == "Custom";
        try
        {
            if (JsonNode.Parse(metadataJson) is not JsonObject metadata
                || metadata["cardSystemCardData"] is not JsonObject cardData
                || !LegacySaveDocument.ReadBool(cardData["isCard"])
                || cardData["categories"] is not JsonArray categories)
                return false;
            return categories.Any(node => LegacySaveDocument.ReadString(node) == category);
        }
        catch (JsonException)
        {
            return false;
        }
    }


    private void ConnectPropertyEditor(Control editor)
    {
        switch (editor)
        {
            case StringArrayEditor stringArrayEditor:
                stringArrayEditor.Changed += RefreshPropertyVisibility;
                break;
            case CheckBox checkBox:
                checkBox.Toggled += _ => RefreshPropertyVisibility();
                break;
            case SpinBox spinBox:
                spinBox.ValueChanged += _ => RefreshPropertyVisibility();
                break;
            case OptionButton optionButton:
                optionButton.ItemSelected += _ => RefreshPropertyVisibility();
                break;
            case ColorPickerButton colorPicker:
                colorPicker.ColorChanged += _ => RefreshPropertyVisibility();
                break;
            case TextEdit textEdit:
                textEdit.TextChanged += RefreshPropertyVisibility;
                break;
            case LineEdit lineEdit:
                lineEdit.TextChanged += _ => RefreshPropertyVisibility();
                break;
        }
    }

    private void RefreshPropertyVisibility()
    {
        foreach (JsonNode? node in _propertyDefinitions)
        {
            if (node is not JsonObject definition)
                continue;
            string propertyName = LegacySaveDocument.ReadString(definition["variableName"]);
            if (!_propertyEditors.TryGetValue(propertyName, out (string Type, Control Editor) field))
                continue;
            bool visible = true;
            if (definition["requires"] is JsonArray requirements)
            {
                foreach (JsonNode? requirement in requirements)
                {
                    if (requirement is not JsonObject requirementObject || !IsRequirementFulfilled(requirementObject))
                    {
                        visible = false;
                        break;
                    }
                }
            }
            Control row = field.Editor is CheckBox ? field.Editor : field.Editor.GetParent() as Control ?? field.Editor;
            row.Visible = visible;
        }
    }

    private bool IsRequirementFulfilled(JsonObject requirement)
    {
        string key = LegacySaveDocument.ReadString(requirement["key"]);
        if (!_propertyEditors.TryGetValue(key, out (string Type, Control Editor) field))
            return false;
        string actual = GetEditorValueString(field.Type, field.Editor);
        string expected = LegacySaveDocument.ReadString(requirement["value"]);
        return LegacySaveDocument.ReadString(requirement["op"]) switch
        {
            "=" => actual == expected,
            "!=" => actual != expected,
            _ => false
        };
    }

    private static string GetEditorValueString(string type, Control editor)
    {
        if (editor is CheckBox checkBox)
            return checkBox.ButtonPressed ? "true" : "false";
        if (editor is SpinBox spinBox)
            return type == "Number"
                ? ((int)Math.Round(spinBox.Value)).ToString(CultureInfo.InvariantCulture)
                : spinBox.Value.ToString(CultureInfo.InvariantCulture);
        if (editor is OptionButton choices)
            return choices.Selected >= 0 ? choices.GetItemMetadata(choices.Selected).AsString() : "";
        if (editor is ColorPickerButton colorPicker)
            return "#" + colorPicker.Color.ToHtml(false);
        if (editor is StringArrayEditor stringArrayEditor)
            return stringArrayEditor.GetValues().ToJsonString();
        if (editor is TextEdit textEdit)
        {
            try
            {
                return JsonNode.Parse(textEdit.Text)?.ToJsonString() ?? "";
            }
            catch (JsonException)
            {
                return "";
            }
        }
        return editor is LineEdit lineEdit ? lineEdit.Text : "";
    }

    private void ApplyAssignments()
    {
        JsonObject? use = GetSelectedUse();
        if (use is null)
            return;
        try
        {
            JsonArray assignments = _rawAssignmentsMode.ButtonPressed
                ? ParseRawAssignments()
                : CreateStructuredAssignments(use);
            JsonObject writableUse = FindSelectedUse(EnsureWritableBrain())
                ?? throw new InvalidDataException("The selected behavior use no longer exists.");
            writableUse["propertyAssignments"] = assignments;
            NotifyChanged(_rawAssignmentsMode.ButtonPressed
                ? "Applied raw property assignments. Save the project to persist them."
                : "Applied structured properties. Save the project to persist them.");
            RefreshAssignedUses();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or OverflowException)
        {
            _status.Text = exception.Message;
        }
    }

    private JsonArray ParseRawAssignments()
    {
        if (JsonNode.Parse(_assignmentsEditor.Text) is not JsonArray assignments)
            throw new InvalidDataException("Property assignments must be a JSON array.");
        foreach (JsonNode? node in assignments)
        {
            if (node is not JsonObject assignment
                || LegacySaveDocument.ReadString(assignment["propertyName"]).Length == 0
                || assignment["valueJson"] is not JsonValue valueNode
                || !valueNode.TryGetValue(out string? valueJson)
                || valueJson is null)
                throw new InvalidDataException("Each assignment needs a propertyName and a JSON-encoded valueJson string.");
            using JsonDocument _ = JsonDocument.Parse(valueJson);
        }
        return assignments;
    }

    private JsonArray CreateStructuredAssignments(JsonObject use)
    {
        if (!_propertyDefinitionsAvailable)
            throw new InvalidDataException("Structured property definitions are unavailable. Switch to raw JSON mode to edit assignments.");
        var assignments = new JsonArray();
        var definedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in _propertyDefinitions)
        {
            if (node is not JsonObject definition)
                continue;
            string name = LegacySaveDocument.ReadString(definition["variableName"]);
            if (name.Length == 0 || !_propertyEditors.TryGetValue(name, out (string Type, Control Editor) editor))
                continue;
            JsonNode? value = ReadEditorValue(name, editor.Type, editor.Editor);
            var encodedValue = new JsonObject { ["value"] = value };
            assignments.Add(new JsonObject { ["propertyName"] = name, ["valueJson"] = encodedValue.ToJsonString() });
            definedNames.Add(name);
        }
        if (use["propertyAssignments"] is JsonArray existing)
        {
            foreach (JsonNode? node in existing)
            {
                if (node is JsonObject assignment)
                {
                    string name = LegacySaveDocument.ReadString(assignment["propertyName"]);
                    if (!definedNames.Contains(name))
                        assignments.Add(assignment.DeepClone());
                }
                else
                {
                    assignments.Add(node?.DeepClone());
                }
            }
        }
        return assignments;
    }

    private static JsonNode? ReadEditorValue(string propertyName, string type, Control editor)
    {
        if (editor is CheckBox checkBox)
            return JsonValue.Create(checkBox.ButtonPressed);
        if (editor is SpinBox spinBox)
            return type == "Number"
                ? JsonValue.Create(checked((int)Math.Round(spinBox.Value)))
                : JsonValue.Create(spinBox.Value);
        if (editor is OptionButton choices)
            return JsonValue.Create(choices.Selected >= 0 ? choices.GetItemMetadata(choices.Selected).AsString() : "");
        if (editor is ColorPickerButton colorPicker)
            return JsonValue.Create("#" + colorPicker.Color.ToHtml(false));
        if (editor is StringArrayEditor stringValuesEditor)
            return stringValuesEditor.GetValues();
        if (editor is TextEdit arrayEditor)
        {
            if (JsonNode.Parse(arrayEditor.Text) is not JsonArray values)
                throw new InvalidDataException($"Property '{propertyName}' must be a JSON array.");
            bool numbers = type == "NumberArray";
            foreach (JsonNode? value in values)
            {
                if (numbers)
                {
                    if (value is not JsonValue number || !number.TryGetValue(out int _))
                        throw new InvalidDataException($"Every value for '{propertyName}' must be an integer.");
                }
                else if (value is not JsonValue text || !text.TryGetValue(out string? _))
                {
                    throw new InvalidDataException($"Every value for '{propertyName}' must be a string.");
                }
            }
            return values;
        }
        return JsonValue.Create(editor is LineEdit lineEdit ? lineEdit.Text : "");
    }

    private static string ReadPropertyString(JsonNode? value)
    {
        if (value is null)
            return "";
        if (value is JsonValue jsonValue && jsonValue.TryGetValue(out string? text))
            return text ?? "";
        return value.ToJsonString();
    }

    private static bool ReadPropertyBoolean(JsonNode? value)
        => value is JsonValue jsonValue && jsonValue.TryGetValue(out bool boolean) && boolean;

    private static double ReadPropertyNumber(JsonNode? value)
    {
        if (value is not JsonValue jsonValue)
            return 0d;
        if (jsonValue.TryGetValue(out double number))
            return number;
        if (jsonValue.TryGetValue(out int integer))
            return integer;
        if (jsonValue.TryGetValue(out long longInteger))
            return longInteger;
        return 0d;
    }

    private void NotifyChanged(string message)
    {
        _onChanged();
        _status.Text = message;
    }

    private JsonObject EnsureWritableBrain()
    {
        JsonObject database = EnsureBehaviorDatabase();
        if (!TryEnsurePairedArrays(database, "brainIds", "brains", out JsonArray ids, out JsonArray brains))
            throw new InvalidDataException("The .voos brainIds and brains tables have different lengths.");

        string brainId = _actor.BrainName;
        int index = FindId(ids, brainId);
        if (index >= 0 && brainId != DefaultBrainId)
            return brains[index] as JsonObject
                ?? throw new InvalidDataException($"Brain '{brainId}' is not an object.");

        JsonObject? original = index >= 0 ? brains[index] as JsonObject : null;
        JsonObject clone = original is null
            ? new JsonObject { ["metadataJson"] = "", ["behaviorUses"] = new JsonArray() }
            : JsonNode.Parse(original.ToJsonString())!.AsObject();
        string cloneId = NewId();
        ids.Add(cloneId);
        brains.Add(clone);
        _actor.Data["brainName"] = cloneId;
        return clone;
    }

    private JsonObject? GetCurrentBrain()
    {
        JsonObject? database = _document.BehaviorDatabase;
        if (!TryGetPairedArrays(database, "brainIds", "brains", out JsonArray ids, out JsonArray brains))
            return null;
        int index = FindId(ids, _actor.BrainName);
        return index >= 0 ? brains[index] as JsonObject : null;
    }

    private JsonObject EnsureBehaviorDatabase()
    {
        if (_document.Root["behaviorDatabase"] is JsonObject database)
            return database;
        if (_document.Root["behaviorDatabase"] is not null)
            throw new InvalidDataException("The .voos behaviorDatabase field is not an object.");
        database = new JsonObject { ["version"] = 4 };
        database["behaviorIds"] = new JsonArray();
        database["behaviors"] = new JsonArray();
        database["brainIds"] = new JsonArray();
        database["brains"] = new JsonArray();
        _document.Root["behaviorDatabase"] = database;
        return database;
    }

    private static JsonArray EnsureBehaviorUses(JsonObject brain)
    {
        if (brain["behaviorUses"] is JsonArray uses)
            return uses;
        uses = new JsonArray();
        brain["behaviorUses"] = uses;
        return uses;
    }

    private void AddEmbeddedBehavior(string id, string label, string source, string metadataJson)
    {
        JsonObject database = EnsureBehaviorDatabase();
        if (!TryEnsurePairedArrays(database, "behaviorIds", "behaviors", out JsonArray ids, out JsonArray behaviors))
            throw new InvalidDataException("The .voos behaviorIds and behaviors tables have different lengths.");
        var behavior = new JsonObject
        {
            ["label"] = label,
            ["javascript"] = source,
            ["draftJavascript"] = "",
            ["userLibraryFile"] = "",
            ["metadataJson"] = metadataJson
        };
        ids.Add(id);
        behaviors.Add(behavior);
    }

    private static JsonObject CreateBehaviorUse(string id, string uri)
        => new()
        {
            ["id"] = id,
            ["behaviorUri"] = uri,
            ["metadataJson"] = "",
            ["propertyAssignments"] = new JsonArray()
        };

    private string GetBehaviorLabel(string uri)
    {
        if (uri.StartsWith("embedded:", StringComparison.Ordinal))
        {
            string id = uri["embedded:".Length..];
            JsonObject? behavior = FindEmbeddedBehavior(id);
            return behavior is null ? $"Missing behavior · {ShortId(id)}" : GetEmbeddedLabel(id, behavior);
        }
        if (_builtinSources.ContainsKey(uri))
            return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(_builtinSources[uri]));
        return uri;
    }

    private static string GetEmbeddedLabel(string id, JsonObject behavior)
    {
        string metadataJson = LegacySaveDocument.ReadString(behavior["metadataJson"]);
        if (metadataJson.Length > 0)
        {
            try
            {
                if (JsonNode.Parse(metadataJson) is JsonObject metadata
                    && metadata["cardSystemCardData"] is JsonObject cardData)
                {
                    string title = LegacySaveDocument.ReadString(cardData["title"]);
                    if (title.Length > 0)
                        return title;
                }
            }
            catch (JsonException)
            {
            }
        }
        string label = LegacySaveDocument.ReadString(behavior["label"]);
        return label.Length == 0 || label == "Custom" ? $"Custom · {ShortId(id)}" : label;
    }

    private string GetBehaviorMetadataJson(string uri)
    {
        if (uri.StartsWith("embedded:", StringComparison.Ordinal)
            && FindEmbeddedBehavior(uri["embedded:".Length..]) is JsonObject embedded)
            return LegacySaveDocument.ReadString(embedded["metadataJson"]);
        if (uri.StartsWith("builtin:", StringComparison.Ordinal)
            && _builtinSources.TryGetValue(uri, out string? path))
        {
            string metadataPath = path + ".metaJson";
            if (Godot.FileAccess.FileExists(metadataPath))
                return Godot.FileAccess.GetFileAsString(metadataPath);
        }
        return CreateDefaultMetadata(GetBehaviorLabel(uri));
    }

    private string GetSelectedMetadataJson() => GetBehaviorMetadataJson(_selectedUri);

    private string FormatBehaviorDetails(string uri)
    {
        try
        {
            JsonObject? metadata = JsonNode.Parse(GetBehaviorMetadataJson(uri)) as JsonObject;
            bool hasBuiltinMetadata = !uri.StartsWith("builtin:", StringComparison.Ordinal)
                || (_builtinSources.TryGetValue(uri, out string? sourcePath) && Godot.FileAccess.FileExists(sourcePath + ".metaJson"));
            JsonObject? panel = metadata?["cardSystemPanelData"] as JsonObject;
            JsonObject? card = metadata?["cardSystemCardData"] as JsonObject;
            if (!hasBuiltinMetadata)
            {
                panel = null;
                card = null;
            }
            string title = LegacySaveDocument.ReadString(card?["title"], LegacySaveDocument.ReadString(panel?["title"], GetBehaviorLabel(uri)));
            string description = LegacySaveDocument.ReadString(card?["description"], LegacySaveDocument.ReadString(panel?["description"]));
            bool isCard = card is not null && LegacySaveDocument.ReadBool(card["isCard"], true);
            string kind = panel is not null && LegacySaveDocument.ReadBool(panel["isPanel"]) ? "Panel" : isCard ? "Card" : "Behavior";
            var details = new List<string> { title, kind };
            if (isCard && card?["categories"] is JsonArray categories && categories.Count > 0)
            {
                string categoryNames = string.Join(", ", categories.Select(category => LegacySaveDocument.ReadString(category)).Where(value => value.Length > 0));
                if (categoryNames.Length > 0)
                    details.Add($"Categories: {categoryNames}");
            }
            if (description.Length > 0)
                details.Add(description);
            return string.Join("\n", details);
        }
        catch (JsonException)
        {
            return GetBehaviorLabel(uri);
        }
    }

    private JsonObject? GetSelectedUse()
        => _selectedUseId.Length == 0 ? null : FindSelectedUse(GetCurrentBrain());

    private JsonObject? FindSelectedUse(JsonObject? brain)
    {
        if (brain?["behaviorUses"] is not JsonArray uses)
            return null;
        foreach (JsonNode? node in uses)
        {
            if (node is JsonObject use && LegacySaveDocument.ReadString(use["id"]) == _selectedUseId)
                return use;
        }
        return null;
    }

    private JsonObject? FindEmbeddedBehavior(string id)
    {
        if (!TryGetPairedArrays(_document.BehaviorDatabase, "behaviorIds", "behaviors", out JsonArray ids, out JsonArray behaviors))
            return null;
        int index = FindId(ids, id);
        return index >= 0 ? behaviors[index] as JsonObject : null;
    }

    private static bool TryGetPairedArrays(JsonObject? parent, string idKey, string valuesKey, out JsonArray ids, out JsonArray values)
    {
        ids = parent?[idKey] as JsonArray ?? new JsonArray();
        values = parent?[valuesKey] as JsonArray ?? new JsonArray();
        return parent?[idKey] is JsonArray && parent[valuesKey] is JsonArray && ids.Count == values.Count;
    }

    private static bool TryEnsurePairedArrays(JsonObject parent, string idKey, string valuesKey, out JsonArray ids, out JsonArray values)
    {
        ids = parent[idKey] as JsonArray ?? new JsonArray();
        values = parent[valuesKey] as JsonArray ?? new JsonArray();
        if (parent[idKey] is null)
            parent[idKey] = ids;
        if (parent[valuesKey] is null)
            parent[valuesKey] = values;
        return parent[idKey] is JsonArray && parent[valuesKey] is JsonArray && ids.Count == values.Count;
    }

    private static int FindId(JsonArray ids, string id)
    {
        for (int index = 0; index < ids.Count; index++)
        {
            if (LegacySaveDocument.ReadString(ids[index]) == id)
                return index;
        }
        return -1;
    }

    private static string CreateDefaultMetadata(string title)
    {
        var categories = new JsonArray();
        categories.Add("Custom");
        var cardData = new JsonObject
        {
            ["isCard"] = true,
            ["title"] = title,
            ["description"] = "A custom Game Builder behavior.",
            ["categories"] = categories,
            ["imageResourcePath"] = "",
            ["listPriority"] = 0,
            ["hidden"] = false
        };
        return new JsonObject { ["cardSystemCardData"] = cardData }.ToJsonString();
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static string ShortId(string id) => id.Length > 8 ? id[..8] : id;
}
