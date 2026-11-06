using System.Text.Json;
using System.Text.Json.Nodes;
using GBNG.Behaviors;
using GBNG.Domain;
using Godot;

namespace GBNG.App;

public sealed partial class LegacyParticleEffectWindow : Window
{
    private readonly Func<JsonArray?> _readParticleEffects;
    private readonly Action<JsonObject?, JsonObject?, string> _saveParticleEffect;
    private readonly Action<string> _reportStatus;
    private readonly ItemList _particleList = new();
    private readonly LineEdit _nameField = new();
    private readonly OptionButton _shapeField = new();
    private readonly SpinBox _durationField = CreateNumberField("ParticleDuration", 0.05, 120, 0.05);
    private readonly SpinBox _speedField = CreateNumberField("ParticleSpeed", 0, 100, 0.05);
    private readonly SpinBox _burstCountField = CreateNumberField("ParticleBurstCount", 1, 4096, 1);
    private readonly SpinBox _shapeSizeField = CreateNumberField("ParticleShapeSize", 0, 100, 0.05);
    private readonly SpinBox _startSizeField = CreateNumberField("ParticleStartSize", 0.01, 100, 0.01);
    private readonly SpinBox _endSizeField = CreateNumberField("ParticleEndSize", 0.01, 100, 0.01);
    private readonly SpinBox _gravityField = CreateNumberField("ParticleGravity", -20, 20, 0.05);
    private readonly VBoxContainer _colorStops = new() { Name = "ParticleColorStops" };
    private readonly Button _newButton = new() { Name = "ParticleNewButton", Text = "New" };
    private readonly Button _copyButton = new() { Name = "ParticleCopyButton", Text = "Copy" };
    private readonly Button _deleteButton = new() { Name = "ParticleDeleteButton", Text = "Delete" };
    private readonly Button _addColorStopButton = new() { Name = "ParticleAddColorStopButton", Text = "Add color stop" };
    private readonly Button _saveButton = new() { Name = "ParticleSaveButton", Text = "Save" };
    private readonly Button _previewButton = new() { Name = "ParticlePreviewButton", Text = "Restart preview" };
    private readonly CheckBox _continuousPreview = new() { Name = "ParticleContinuousPreview", Text = "Continuous preview" };
    private readonly Label _status = new() { Name = "ParticleEditorStatus", AutowrapMode = TextServer.AutowrapMode.Word };
    private readonly ConfirmationDialog _deleteConfirmation = new() { Name = "ParticleDeleteConfirmation" };
    private readonly SubViewport _previewViewport = new()
    {
        Name = "ParticlePreviewViewport",
        Size = new Vector2I(220, 200),
        RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible,
        OwnWorld3D = true
    };
    private JsonObject _draftContent = new();
    private JsonObject? _originalSojo;
    private List<ColorStopDraft> _colorStopDrafts = new();
    private string? _selectedId;
    private bool _isNew;
    private bool _shapeChanged;
    private readonly HashSet<string> _changedContentFields = new(StringComparer.Ordinal);
    private bool _refreshing;
    private bool _draftDirty;
    private bool _contentValid = true;
    private Node3D _previewRoot = null!;
    private Camera3D _previewCamera = null!;
    private CpuParticles3D? _previewParticles;

    private sealed record ColorStopDraft(string Id, Color Value, float Position, JsonObject? Original);

    public LegacyParticleEffectWindow(
        Func<JsonArray?> readParticleEffects,
        Action<JsonObject?, JsonObject?, string> saveParticleEffect,
        Action<string> reportStatus)
    {
        _readParticleEffects = readParticleEffects;
        _saveParticleEffect = saveParticleEffect;
        _reportStatus = reportStatus;
        Name = "ParticleEffectEditorWindow";
        Title = "Particle effects";
        Size = new Vector2I(1220, 820);
        Hide();
        CloseRequested += CloseEditor;
        VisibilityChanged += OnVisibilityChanged;
        BuildInterface();
        AddChild(_deleteConfirmation);
        _deleteConfirmation.Title = "Delete particle effect";
        _deleteConfirmation.Confirmed += DeleteCurrentParticleEffect;
        BuildPreviewScene();
        RefreshFromDocument();
    }

    internal void RefreshFromDocument()
    {
        string? selectedId = _selectedId;
        List<JsonObject> effects = GetParticleEffects();
        _refreshing = true;
        _particleList.Clear();
        int selectedIndex = -1;
        for (int index = 0; index < effects.Count; index++)
        {
            JsonObject effect = effects[index];
            string id = LegacySaveDocument.ReadString(effect["id"]);
            string name = LegacySaveDocument.ReadString(effect["name"], id);
            _particleList.AddItem(name);
            _particleList.SetItemMetadata(index, id);
            if (id == selectedId)
                selectedIndex = index;
        }

        if (selectedIndex < 0 && effects.Count > 0 && !_isNew)
            selectedIndex = 0;
        if (selectedIndex >= 0)
        {
            _particleList.Select(selectedIndex);
            LoadParticleEffect(effects[selectedIndex]);
        }
        else if (!_isNew)
        {
            _selectedId = null;
            _originalSojo = null;
            SetEmptyDraft();
        }
        _refreshing = false;
        UpdateButtonState();
    }

    internal void FocusParticleEffect(string id)
    {
        _selectedId = id;
        _isNew = false;
        RefreshFromDocument();
    }

    private void BuildInterface()
    {
        var margin = new MarginContainer { AnchorRight = 1f, AnchorBottom = 1f };
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        AddChild(margin);

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 14);
        margin.AddChild(columns);

        var library = new PanelContainer { CustomMinimumSize = new Vector2(200f, 0f) };
        columns.AddChild(library);
        var libraryLayout = new VBoxContainer();
        libraryLayout.AddThemeConstantOverride("separation", 8);
        library.AddChild(libraryLayout);
        libraryLayout.AddChild(new Label { Text = "Particle effects", HorizontalAlignment = HorizontalAlignment.Center });
        _particleList.Name = "ParticleEffectList";
        _particleList.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _particleList.ItemSelected += SelectParticleEffect;
        libraryLayout.AddChild(_particleList);
        var libraryActions = new HBoxContainer();
        _newButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _newButton.Pressed += BeginNewParticleEffect;
        _copyButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _copyButton.Pressed += CopySelectedParticleEffect;
        libraryActions.AddChild(_newButton);
        libraryActions.AddChild(_copyButton);
        libraryLayout.AddChild(libraryActions);
        _deleteButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _deleteButton.Pressed += RequestDeleteParticleEffect;
        libraryLayout.AddChild(_deleteButton);

        var editorScroll = new ScrollContainer
        {
            Name = "ParticleEditorScroll",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        columns.AddChild(editorScroll);
        var editor = new VBoxContainer { CustomMinimumSize = new Vector2(460f, 0f) };
        editor.AddThemeConstantOverride("separation", 10);
        editorScroll.AddChild(editor);
        editor.AddChild(new Label
        {
            Text = "Create and tune legacy particle effects. Changes preserve unedited .voos fields and are recorded in Undo/Redo.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });

        _nameField.Name = "ParticleEffectName";
        _nameField.TextChanged += _ => MarkDraftChanged();
        AddEditorRow(editor, "Name", _nameField);

        string[] shapeNames = ["Cone", "Sphere", "Box", "Circle", "Rectangle"];
        for (int index = 0; index < shapeNames.Length; index++)
            _shapeField.AddItem(shapeNames[index], index);
        _shapeField.Name = "ParticleShape";
        _shapeField.ItemSelected += _ =>
        {
            _shapeChanged = true;
            MarkDraftChanged();
        };
        AddEditorRow(editor, "Emission shape", _shapeField);

        AddEditorRow(editor, "Duration · seconds", _durationField);
        AddEditorRow(editor, "Speed", _speedField);
        AddEditorRow(editor, "Burst count", _burstCountField);
        AddEditorRow(editor, "Shape size", _shapeSizeField);
        AddEditorRow(editor, "Start size", _startSizeField);
        AddEditorRow(editor, "End size", _endSizeField);
        AddEditorRow(editor, "Gravity modifier", _gravityField);

        _durationField.ValueChanged += _ => MarkDraftChanged("duration");
        _speedField.ValueChanged += _ => MarkDraftChanged("speed");
        _burstCountField.ValueChanged += _ => MarkDraftChanged("burstCount");
        _shapeSizeField.ValueChanged += _ => MarkDraftChanged("shapeSize");
        _startSizeField.ValueChanged += _ => MarkDraftChanged("startSize");
        _endSizeField.ValueChanged += _ => MarkDraftChanged("endSize");
        _gravityField.ValueChanged += _ => MarkDraftChanged("gravityModifier");

        editor.AddChild(new Label { Text = "Color over lifetime" });
        _colorStops.AddThemeConstantOverride("separation", 6);
        editor.AddChild(_colorStops);
        _addColorStopButton.Pressed += AddColorStop;
        editor.AddChild(_addColorStopButton);

        var editorActions = new HBoxContainer();
        _saveButton.Pressed += () => SaveDraft();
        editorActions.AddChild(_saveButton);
        editorActions.AddChild(_status);
        editor.AddChild(editorActions);

        var previewPanel = new PanelContainer { CustomMinimumSize = new Vector2(220f, 0f) };
        columns.AddChild(previewPanel);
        var previewLayout = new VBoxContainer();
        previewLayout.AddThemeConstantOverride("separation", 8);
        previewPanel.AddChild(previewLayout);
        previewLayout.AddChild(new Label { Text = "Live preview", HorizontalAlignment = HorizontalAlignment.Center });
        var previewContainer = new SubViewportContainer
        {
            Name = "ParticlePreviewContainer",
            CustomMinimumSize = new Vector2(220f, 200f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            Stretch = true
        };
        previewContainer.AddChild(_previewViewport);
        previewLayout.AddChild(previewContainer);
        _continuousPreview.Toggled += _ => RefreshPreview();
        previewLayout.AddChild(_continuousPreview);
        _previewButton.Pressed += RefreshPreview;
        previewLayout.AddChild(_previewButton);
        previewLayout.AddChild(new Label
        {
            Text = "Local preview · Save applies changes.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });

        UpdateButtonState();
    }

    private void BuildPreviewScene()
    {
        _previewRoot = new Node3D { Name = "ParticlePreviewScene" };
        _previewViewport.AddChild(_previewRoot);
        _previewRoot.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color("#182431"),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color("#c5d7e8"),
                AmbientLightEnergy = 0.8f
            }
        });
        _previewCamera = new Camera3D { Name = "PreviewCamera", Current = true, Fov = 45f };
        _previewRoot.AddChild(_previewCamera);
        _previewCamera.LookAtFromPosition(new Vector3(0f, 0f, 7.5f), Vector3.Zero, Vector3.Up);
        _previewRoot.AddChild(new DirectionalLight3D
        {
            Name = "PreviewLight",
            RotationDegrees = new Vector3(-45f, -25f, 0f),
            LightEnergy = 1.2f
        });
    }

    private static SpinBox CreateNumberField(string name, double minimum, double maximum, double step)
    {
        var field = new SpinBox
        {
            Name = name,
            MinValue = minimum,
            MaxValue = maximum,
            Step = step,
            CustomMinimumSize = new Vector2(120f, 0f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        return field;
    }

    private static void AddEditorRow(VBoxContainer layout, string label, Control editor)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(120f, 0f) });
        editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(editor);
        layout.AddChild(row);
    }

    private List<JsonObject> GetParticleEffects()
    {
        var effects = new List<JsonObject>();
        JsonArray? sojos = _readParticleEffects();
        if (sojos is null)
            return effects;
        foreach (JsonNode? node in sojos)
        {
            if (node is JsonObject sojo && LegacySaveDocument.ReadString(sojo["contentType"]) == "ParticleEffect")
                effects.Add(sojo);
        }
        effects.Sort(static (left, right) =>
        {
            int nameOrder = StringComparer.OrdinalIgnoreCase.Compare(
                LegacySaveDocument.ReadString(left["name"]),
                LegacySaveDocument.ReadString(right["name"]));
            return nameOrder != 0
                ? nameOrder
                : StringComparer.Ordinal.Compare(LegacySaveDocument.ReadString(left["id"]), LegacySaveDocument.ReadString(right["id"]));
        });
        return effects;
    }

    private void SelectParticleEffect(long index)
    {
        if (_refreshing)
            return;
        string id = _particleList.GetItemMetadata((int)index).AsString();
        if (!SaveDraft())
        {
            _refreshing = true;
            RestoreListSelection();
            _refreshing = false;
            return;
        }
        JsonObject? effect = GetParticleEffects().FirstOrDefault(candidate => LegacySaveDocument.ReadString(candidate["id"]) == id);
        if (effect is null)
        {
            RefreshFromDocument();
            return;
        }
        _refreshing = true;
        LoadParticleEffect(effect);
        _refreshing = false;
        UpdateButtonState();
    }

    private void RestoreListSelection()
    {
        for (int index = 0; index < _particleList.ItemCount; index++)
        {
            if (_particleList.GetItemMetadata(index).AsString() == _selectedId)
            {
                _particleList.Select(index);
                return;
            }
        }
        _particleList.DeselectAll();
    }

    private void LoadParticleEffect(JsonObject sojo)
    {
        _selectedId = LegacySaveDocument.ReadString(sojo["id"]);
        _originalSojo = sojo.DeepClone().AsObject();
        _isNew = false;
        _shapeChanged = false;
        string serializedContent = LegacySaveDocument.ReadString(sojo["content"]);
        _contentValid = true;
        try
        {
            if (serializedContent.Length == 0)
            {
                _draftContent = new JsonObject();
                _contentValid = true;
            }
            else if (JsonNode.Parse(serializedContent) is JsonObject parsedContent)
            {
                _draftContent = parsedContent;
                _contentValid = true;
            }
            else
            {
                _draftContent = new JsonObject();
                _contentValid = false;
            }
        }
        catch (JsonException)
        {
            _draftContent = new JsonObject();
            _contentValid = false;
        }
        _colorStopDrafts = ReadColorStops(_draftContent["colorOverLifetime"] as JsonArray);
        _changedContentFields.Clear();
        _nameField.Text = LegacySaveDocument.ReadString(sojo["name"], "Particle effect");
        _shapeField.Select(Math.Clamp(ReadInt(_draftContent["shapeType"], 1), 0, 4));
        _durationField.Value = FitRange(ReadFloat(_draftContent["duration"], 1f), _durationField);
        _speedField.Value = FitRange(ReadFloat(_draftContent["speed"], 1f), _speedField);
        _burstCountField.Value = FitRange(ReadInt(_draftContent["burstCount"], 10), _burstCountField);
        _shapeSizeField.Value = FitRange(ReadFloat(_draftContent["shapeSize"], 1f), _shapeSizeField);
        _startSizeField.Value = FitRange(ReadFloat(_draftContent["startSize"], 1f), _startSizeField);
        _endSizeField.Value = FitRange(ReadFloat(_draftContent["endSize"], 1f), _endSizeField);
        _gravityField.Value = FitRange(ReadFloat(_draftContent["gravityModifier"], 0f), _gravityField);
        _draftDirty = false;
        _status.Text = _contentValid ? string.Empty : "This effect has malformed content; it is read-only to avoid overwriting data.";
        RefreshColorStops();
        SetEditorEnabled(_contentValid);
        RefreshPreview();
    }

    private void BeginNewParticleEffect()
    {
        if (!SaveDraft())
            return;
        _refreshing = true;
        _particleList.DeselectAll();
        _selectedId = Guid.NewGuid().ToString();
        _originalSojo = null;
        _isNew = true;
        _contentValid = true;
        _draftDirty = false;
        _shapeChanged = false;
        _changedContentFields.Clear();
        _draftContent = CreateDefaultContent();
        _colorStopDrafts = ReadColorStops(_draftContent["colorOverLifetime"] as JsonArray);
        _nameField.Text = "My particle effect";
        _shapeField.Select(1);
        _durationField.Value = 1;
        _speedField.Value = 1;
        _burstCountField.Value = 10;
        _shapeSizeField.Value = 1;
        _startSizeField.Value = 1;
        _endSizeField.Value = 1;
        _gravityField.Value = 0;
        _status.Text = "New particle effect · save to add it to the project.";
        RefreshColorStops();
        SetEditorEnabled(true);
        RefreshPreview();
        _refreshing = false;
        UpdateButtonState();
    }

    private void CopySelectedParticleEffect()
    {
        if (!SaveDraft() || _selectedId is null || !_contentValid)
            return;
        JsonObject? source = GetParticleEffects().FirstOrDefault(effect => LegacySaveDocument.ReadString(effect["id"]) == _selectedId);
        if (source is null)
            return;
        JsonObject copy = source.DeepClone().AsObject();
        string id = Guid.NewGuid().ToString();
        string name = LegacySaveDocument.ReadString(source["name"], "Particle effect") + " copy";
        copy["id"] = id;
        copy["name"] = name;
        _selectedId = id;
        _originalSojo = copy.DeepClone().AsObject();
        _isNew = false;
        _draftDirty = false;
        _saveParticleEffect(null, copy, $"Copy particle effect {name}");
        _reportStatus($"Copied particle effect '{name}'.");
    }

    private void SetEmptyDraft()
    {
        _contentValid = true;
        _draftContent = new JsonObject();
        _colorStopDrafts.Clear();
        _nameField.Text = string.Empty;
        _shapeField.Select(1);
        _durationField.Value = 1;
        _speedField.Value = 1;
        _burstCountField.Value = 10;
        _shapeSizeField.Value = 1;
        _startSizeField.Value = 1;
        _endSizeField.Value = 1;
        _gravityField.Value = 0;
        _draftDirty = false;
        _shapeChanged = false;
        _changedContentFields.Clear();
        _status.Text = "Create a particle effect or select one from the library.";
        RefreshColorStops();
        SetEditorEnabled(false);
        RefreshPreview();
    }

    private bool SaveDraft()
    {
        if (_selectedId is null || (!_isNew && _originalSojo is null) || !_draftDirty && !_isNew)
            return true;
        if (!_contentValid)
            return true;
        string name = _nameField.Text.Trim();
        if (name.Length == 0)
        {
            _status.Text = "Enter a name before saving.";
            return false;
        }

        JsonObject after = BuildSojo(name);
        JsonObject? before = _originalSojo?.DeepClone().AsObject();
        string label = _isNew ? $"Add particle effect {name}" : $"Edit particle effect {name}";
        try
        {
            _saveParticleEffect(before, after, label);
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not save this particle effect: {exception.Message}";
            return false;
        }
        _originalSojo = after.DeepClone().AsObject();
        _isNew = false;
        _draftDirty = false;
        _reportStatus($"Saved particle effect '{name}'.");
        RefreshFromDocument();
        return true;
    }

    private JsonObject BuildSojo(string name)
    {
        JsonObject sojo = _originalSojo?.DeepClone().AsObject() ?? new JsonObject
        {
            ["id"] = _selectedId,
            ["contentType"] = "ParticleEffect"
        };
        sojo["name"] = name;
        JsonObject content = BuildContent();
        sojo["content"] = content.ToJsonString();
        return sojo;
    }

    private JsonObject BuildContent()
    {
        JsonObject content = _draftContent.DeepClone().AsObject();
        if (_isNew || _changedContentFields.Contains("duration"))
            content["duration"] = _durationField.Value;
        if (_isNew || _changedContentFields.Contains("speed"))
            content["speed"] = _speedField.Value;
        if (_isNew || _changedContentFields.Contains("burstCount"))
            content["burstCount"] = (int)_burstCountField.Value;
        if (_isNew || _changedContentFields.Contains("shapeSize"))
            content["shapeSize"] = _shapeSizeField.Value;
        if (_isNew || _changedContentFields.Contains("startSize"))
            content["startSize"] = _startSizeField.Value;
        if (_isNew || _changedContentFields.Contains("endSize"))
            content["endSize"] = _endSizeField.Value;
        if (_isNew || _changedContentFields.Contains("gravityModifier"))
            content["gravityModifier"] = _gravityField.Value;
        if (_isNew || _shapeChanged)
            content["shapeType"] = (int)_shapeField.GetSelectedId();
        if (_isNew || _changedContentFields.Contains("colorOverLifetime"))
        {
            var colorStops = new JsonArray();
            _colorStopDrafts.Sort(static (left, right) => left.Position.CompareTo(right.Position));
            foreach (ColorStopDraft stop in _colorStopDrafts)
            {
                JsonObject stopData = stop.Original?.DeepClone().AsObject() ?? new JsonObject();
                stopData["id"] = stop.Id;
                stopData["position"] = stop.Position;
                JsonObject valueData = stopData["value"]?.DeepClone().AsObject() ?? new JsonObject();
                valueData["r"] = stop.Value.R;
                valueData["g"] = stop.Value.G;
                valueData["b"] = stop.Value.B;
                valueData["a"] = stop.Value.A;
                stopData["value"] = valueData;
                colorStops.Add(stopData);
            }
            content["colorOverLifetime"] = colorStops;
        }
        return content;
    }

    private void AddColorStop()
    {
        float position = _colorStopDrafts.Count == 0 ? 0f : _colorStopDrafts.Count == 1 ? 1f : 0.5f;
        _colorStopDrafts.Add(new ColorStopDraft(Guid.NewGuid().ToString(), Colors.White, position, null));
        RefreshColorStops();
        MarkDraftChanged("colorOverLifetime");
    }

    private void RemoveColorStop(string id)
    {
        if (_colorStopDrafts.Count <= 1)
            return;
        _colorStopDrafts.RemoveAll(stop => stop.Id == id);
        RefreshColorStops();
        MarkDraftChanged("colorOverLifetime");
    }

    private void RefreshColorStops()
    {
        foreach (Node child in _colorStops.GetChildren())
        {
            _colorStops.RemoveChild(child);
            child.QueueFree();
        }
        if (_colorStopDrafts.Count == 0)
            _colorStops.AddChild(new Label { Text = "No color stops" });
        for (int index = 0; index < _colorStopDrafts.Count; index++)
        {
            string stopId = _colorStopDrafts[index].Id;
            var row = new HBoxContainer { Name = $"ParticleColorStop{index}" };
            row.AddChild(new Label { Text = $"Stop {index + 1}", CustomMinimumSize = new Vector2(66f, 0f) });
            var color = new ColorPickerButton
            {
                Name = $"ParticleColorValue{index}",
                Color = _colorStopDrafts[index].Value,
                CustomMinimumSize = new Vector2(70f, 0f),
                Disabled = !_contentValid
            };
            color.ColorChanged += value => UpdateColorStop(stopId, value);
            row.AddChild(color);
            float stopPosition = _colorStopDrafts[index].Position;
            var position = CreateNumberField(
                $"ParticleColorPosition{index}",
                Math.Min(0f, stopPosition),
                Math.Max(1f, stopPosition),
                0.01);
            position.Value = stopPosition;
            position.Editable = _contentValid;
            position.ValueChanged += value => UpdateColorStopPosition(stopId, (float)value);
            row.AddChild(position);
            var remove = new Button
            {
                Name = $"ParticleRemoveColorStop{index}",
                Text = "Remove",
                Disabled = !_contentValid || _colorStopDrafts.Count <= 1
            };
            remove.Pressed += () => RemoveColorStop(stopId);
            row.AddChild(remove);
            _colorStops.AddChild(row);
        }
        _addColorStopButton.Disabled = !_contentValid || _selectedId is null;
    }

    private void UpdateColorStop(string id, Color value)
    {
        int index = _colorStopDrafts.FindIndex(stop => stop.Id == id);
        if (index < 0)
            return;
        _colorStopDrafts[index] = _colorStopDrafts[index] with { Value = value };
        MarkDraftChanged("colorOverLifetime");
    }

    private void UpdateColorStopPosition(string id, float position)
    {
        int index = _colorStopDrafts.FindIndex(stop => stop.Id == id);
        if (index < 0)
            return;
        _colorStopDrafts[index] = _colorStopDrafts[index] with { Position = position };
        MarkDraftChanged("colorOverLifetime");
    }

    private void RequestDeleteParticleEffect()
    {
        if (_selectedId is null || _isNew || !SaveDraft())
            return;
        JsonObject? effect = GetParticleEffects().FirstOrDefault(candidate => LegacySaveDocument.ReadString(candidate["id"]) == _selectedId);
        if (effect is null)
            return;
        _deleteConfirmation.DialogText = $"Delete '{LegacySaveDocument.ReadString(effect["name"])}'? You can undo this from the main toolbar.";
        _deleteConfirmation.PopupCentered(new Vector2I(460, 150));
    }

    private void DeleteCurrentParticleEffect()
    {
        if (_selectedId is null)
            return;
        JsonObject? effect = GetParticleEffects().FirstOrDefault(candidate => LegacySaveDocument.ReadString(candidate["id"]) == _selectedId);
        if (effect is null)
            return;
        string name = LegacySaveDocument.ReadString(effect["name"]);
        _saveParticleEffect(effect.DeepClone().AsObject(), null, $"Delete particle effect {name}");
        _selectedId = null;
        _originalSojo = null;
        _isNew = false;
        RefreshFromDocument();
        _reportStatus($"Deleted particle effect '{name}'. Undo is available from the main toolbar.");
    }

    private static JsonObject CreateDefaultContent()
    {
        var colors = new JsonArray
        {
            CreateColorStop(Colors.Cyan, 0f),
            CreateColorStop(Colors.Cyan, 1f)
        };
        var rotation = new JsonArray
        {
            new JsonObject { ["value"] = 2f, ["position"] = 0f, ["id"] = Guid.NewGuid().ToString() },
            new JsonObject { ["value"] = 2f, ["position"] = 1f, ["id"] = Guid.NewGuid().ToString() }
        };
        return new JsonObject
        {
            ["isLocal"] = false,
            ["duration"] = 1f,
            ["speed"] = 1f,
            ["startSize"] = 1f,
            ["endSize"] = 1f,
            ["shapeType"] = 1,
            ["shapeSize"] = 1f,
            ["velocityOverLifetimeDEPRECATED"] = new JsonArray(),
            ["rotationOverLifetime"] = rotation,
            ["sizeOverLifetimeDEPRECATED"] = new JsonArray(),
            ["colorOverLifetime"] = colors,
            ["burstCount"] = 10,
            ["gravityModifier"] = 0f
        };
    }

    private static JsonObject CreateColorStop(Color color, float position)
        => new()
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["position"] = position,
            ["value"] = new JsonObject
            {
                ["r"] = color.R,
                ["g"] = color.G,
                ["b"] = color.B,
                ["a"] = color.A
            }
        };

    private static List<ColorStopDraft> ReadColorStops(JsonArray? colors)
    {
        var stops = new List<ColorStopDraft>();
        if (colors is null)
            return stops;
        foreach (JsonNode? node in colors)
        {
            if (node is not JsonObject stop || stop["value"] is not JsonObject value)
                continue;
            string id = LegacySaveDocument.ReadString(stop["id"]);
            if (id.Length == 0)
                id = Guid.NewGuid().ToString();
            stops.Add(new ColorStopDraft(
                id,
                new Color(
                    ReadFloat(value["r"], 1f),
                    ReadFloat(value["g"], 1f),
                    ReadFloat(value["b"], 1f),
                    ReadFloat(value["a"], 1f)),
                ReadFloat(stop["position"], 0f),
                stop.DeepClone().AsObject()));
        }
        return stops;
    }

    private void MarkDraftChanged(string? fieldName = null)
    {
        if (_refreshing || !_contentValid || _selectedId is null)
            return;
        if (fieldName is not null)
            _changedContentFields.Add(fieldName);
        _draftDirty = true;
        _status.Text = "Unsaved changes · use Save to apply them to the project.";
        UpdateButtonState();
        RefreshPreview();
    }

    private void SetEditorEnabled(bool enabled)
    {
        _nameField.Editable = enabled && _selectedId is not null;
        _shapeField.Disabled = !enabled || _selectedId is null;
        foreach (SpinBox field in new[]
        {
            _durationField, _speedField, _burstCountField, _shapeSizeField,
            _startSizeField, _endSizeField, _gravityField
        })
            field.Editable = enabled && _selectedId is not null;
        _addColorStopButton.Disabled = !enabled || _selectedId is null;
    }

    private void UpdateButtonState()
    {
        bool hasSelection = _selectedId is not null;
        _copyButton.Disabled = !hasSelection || _isNew || !_contentValid;
        _deleteButton.Disabled = !hasSelection || _isNew;
        _saveButton.Disabled = !hasSelection || !_contentValid || (!_draftDirty && !_isNew);
        _addColorStopButton.Disabled = !hasSelection || !_contentValid;
    }

    private void RefreshPreview()
    {
        if (!IsInstanceValid(_previewRoot) || !IsInsideTree())
            return;
        if (_previewParticles is not null && IsInstanceValid(_previewParticles))
        {
            _previewRoot.RemoveChild(_previewParticles);
            _previewParticles.QueueFree();
            _previewParticles = null;
        }
        if (!Visible || !_contentValid || _selectedId is null)
            return;
        JsonObject previewContent = BuildContent();
        float previewDistance = Math.Max(5.5f, ReadFloat(previewContent["shapeSize"], 1f) * 6f);
        _previewCamera.LookAtFromPosition(new Vector3(0f, 0f, previewDistance), Vector3.Zero, Vector3.Up);
        _previewParticles = LegacyParticleEffectFactory.Create(previewContent, _continuousPreview.ButtonPressed, maximumParticles: 192);
        _previewParticles.Name = "ParticleEffectPreview";
        _previewRoot.AddChild(_previewParticles);
    }

    private void OnVisibilityChanged()
    {
        if (!Visible)
        {
            if (_previewParticles is not null && IsInstanceValid(_previewParticles))
                _previewParticles.Emitting = false;
            return;
        }
        RefreshPreview();
    }

    private void CloseEditor()
    {
        if (!SaveDraft())
            return;
        Hide();
    }

    private static double FitRange(float value, SpinBox field)
    {
        if (value < field.MinValue)
            field.MinValue = value;
        if (value > field.MaxValue)
            field.MaxValue = value;
        return value;
    }

    private static float ReadFloat(JsonNode? node, float fallback)
    {
        try
        {
            return LegacySaveDocument.ReadFloat(node, fallback);
        }
        catch (InvalidDataException)
        {
            return fallback;
        }
    }

    private static int ReadInt(JsonNode? node, int fallback)
    {
        try
        {
            return LegacySaveDocument.ReadInt(node, fallback);
        }
        catch (InvalidDataException)
        {
            return fallback;
        }
    }
}
