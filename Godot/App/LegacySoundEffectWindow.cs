using System.Text.Json;
using System.Text.Json.Nodes;
using GBNG.Audio;
using GBNG.Behaviors;
using GBNG.Domain;
using Godot;

namespace GBNG.App;

public sealed partial class LegacySoundEffectWindow : Window
{
    private const int SliceCount = 32;
    private const int MaximumVolume = 16;
    private const int MaximumPitch = 64;

    private readonly Func<JsonArray?> _readSoundEffects;
    private readonly Action<JsonObject?, JsonObject?, string> _saveSoundEffect;
    private readonly Action<string?> _requestAudioImport;
    private readonly Func<JsonObject, AudioStream?> _loadLocalAudio;
    private readonly Action<string> _reportStatus;
    private readonly ItemList _soundList = new();
    private readonly LineEdit _nameField = new();
    private readonly OptionButton _waveShape = new();
    private readonly HSlider _speed = new();
    private readonly Label _speedValue = new();
    private readonly CheckBox _spatialized = new();
    private readonly LegacySynthEnvelopeEditor _volumeEnvelope = new() { Name = "SynthVolumeEnvelope" };
    private readonly LegacySynthEnvelopeEditor _pitchEnvelope = new() { Name = "SynthPitchEnvelope" };
    private readonly VBoxContainer _synthControls = new() { Name = "SynthSoundControls" };
    private readonly Label _audioInfo = new() { Name = "ImportedAudioInfo", AutowrapMode = TextServer.AutowrapMode.Word };
    private readonly Button _saveButton = new() { Name = "SynthSaveButton", Text = "Save" };
    private readonly Button _deleteButton = new() { Name = "SynthDeleteButton", Text = "Delete" };
    private readonly Button _previewButton = new() { Name = "SynthPreviewButton", Text = "Preview" };
    private readonly Button _importAudioButton = new() { Name = "LocalAudioImportButton", Text = "Import audio…" };
    private readonly Button _replaceAudioButton = new() { Name = "ReplaceAudioButton", Text = "Import / replace audio…" };
    private readonly Label _status = new() { Name = "SynthSoundStatus", AutowrapMode = TextServer.AutowrapMode.Word };
    private readonly ConfirmationDialog _deleteConfirmation = new();
    private readonly AudioStreamPlayer _previewPlayer = new() { Name = "SynthSoundPreview", Bus = LegacyAudioSettings.SfxBusName, VolumeDb = -1.9382f };
    private int[] _volumeValues = Array.Empty<int>();
    private int[] _pitchValues = Array.Empty<int>();
    private JsonObject? _originalSojo;
    private string? _selectedId;
    private bool _isNew;
    private bool _isSynthSound = true;
    private bool _refreshing;

    public LegacySoundEffectWindow(
        Func<JsonArray?> readSoundEffects,
        Action<JsonObject?, JsonObject?, string> saveSoundEffect,
        Action<string?> requestAudioImport,
        Func<JsonObject, AudioStream?> loadLocalAudio,
        Action<string> reportStatus)
    {
        _readSoundEffects = readSoundEffects;
        _saveSoundEffect = saveSoundEffect;
        _requestAudioImport = requestAudioImport;
        _loadLocalAudio = loadLocalAudio;
        _reportStatus = reportStatus;
        Name = "SoundEffectEditorWindow";
        Title = "Sound effects";
        Size = new Vector2I(1040, 760);
        Hide();
        CloseRequested += CloseEditor;
        VisibilityChanged += StopPreviewWhenHidden;
        BuildInterface();
        AddChild(_previewPlayer);
        _deleteConfirmation.Title = "Delete sound effect";
        _deleteConfirmation.Confirmed += DeleteCurrentSound;
        AddChild(_deleteConfirmation);
        RefreshFromDocument();
    }

    internal void RefreshFromDocument()
    {
        string? selectedId = _selectedId;
        List<JsonObject> sounds = GetSoundEffects();
        _refreshing = true;
        _soundList.Clear();
        int selectedIndex = -1;
        for (int index = 0; index < sounds.Count; index++)
        {
            JsonObject sound = sounds[index];
            string id = LegacySaveDocument.ReadString(sound["id"]);
            string name = LegacySaveDocument.ReadString(sound["name"], id);
            _soundList.AddItem($"{name} · {GetSoundKindLabel(sound)}");
            _soundList.SetItemMetadata(index, id);
            if (id == selectedId)
                selectedIndex = index;
        }

        if (selectedIndex < 0 && sounds.Count > 0 && !_isNew)
            selectedIndex = 0;
        if (selectedIndex >= 0)
        {
            _soundList.Select(selectedIndex);
            LoadSound(sounds[selectedIndex]);
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

    internal void FocusSoundEffect(string id)
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

        var library = new PanelContainer { CustomMinimumSize = new Vector2(220f, 0f) };
        columns.AddChild(library);
        var libraryLayout = new VBoxContainer();
        libraryLayout.AddThemeConstantOverride("separation", 8);
        library.AddChild(libraryLayout);
        libraryLayout.AddChild(new Label { Text = "Sound effects", HorizontalAlignment = HorizontalAlignment.Center });
        _soundList.Name = "SynthSoundList";
        _soundList.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _soundList.ItemSelected += SelectSound;
        libraryLayout.AddChild(_soundList);
        var libraryButtons = new HBoxContainer();
        _newButton = new Button { Name = "SynthNewButton", Text = "New synth", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _newButton.Pressed += BeginNewSound;
        _importAudioButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _importAudioButton.Pressed += ImportNewAudio;
        libraryButtons.AddChild(_newButton);
        libraryButtons.AddChild(_importAudioButton);
        libraryLayout.AddChild(libraryButtons);
        _deleteButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _deleteButton.Pressed += RequestDeleteSound;
        libraryLayout.AddChild(_deleteButton);

        var scroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        columns.AddChild(scroll);
        var editor = new VBoxContainer { CustomMinimumSize = new Vector2(660f, 0f) };
        editor.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(editor);
        editor.AddChild(new Label
        {
            Text = "Edit synthesized effects or import local WAV and Ogg Vorbis sounds. Replace legacy Steam Workshop audio with a local file to restore playback.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });
        AddEditorRow(editor, "Name", _nameField);
        _nameField.Name = "SynthSoundName";
        _nameField.TextChanged += _ => MarkDraftChanged();

        foreach ((string text, int value) in new[]
        {
            ("Square", 0),
            ("Triangle", 1),
            ("Sine", 2),
            ("Noise", 3)
        })
            _waveShape.AddItem(text, value);
        _waveShape.Name = "SynthWaveShape";
        _waveShape.ItemSelected += _ => MarkDraftChanged();
        AddEditorRow(_synthControls, "Wave shape", _waveShape);

        var speedRow = new HBoxContainer();
        _speed.Name = "SynthPlaybackSpeed";
        _speed.MinValue = 0;
        _speed.MaxValue = 16;
        _speed.Step = 1;
        _speed.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _speed.ValueChanged += value =>
        {
            _speedValue.Text = ((int)value).ToString();
            MarkDraftChanged();
        };
        _speedValue.CustomMinimumSize = new Vector2(36f, 0f);
        _speedRowLabel = new Label { Text = "0–16" };
        speedRow.AddChild(_speedRowLabel);
        speedRow.AddChild(_speed);
        speedRow.AddChild(_speedValue);
        AddEditorRow(_synthControls, "Playback speed", speedRow);

        _spatialized.Name = "SynthSpatialized";
        _spatialized.Text = "Spatialized (3D)";
        _spatialized.Toggled += _ => MarkDraftChanged();
        editor.AddChild(_spatialized);

        var envelopes = new HBoxContainer();
        envelopes.AddThemeConstantOverride("separation", 12);
        var volumeColumn = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        volumeColumn.AddChild(new Label { Text = "Volume · 32 slices · 0–16" });
        _volumeEnvelope.Minimum = 0;
        _volumeEnvelope.Maximum = MaximumVolume;
        _volumeEnvelope.CustomMinimumSize = new Vector2(320f, 120f);
        _volumeEnvelope.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _volumeEnvelope.ValueEdited += (index, value) =>
        {
            _volumeValues[index] = value;
            MarkDraftChanged();
        };
        volumeColumn.AddChild(_volumeEnvelope);
        envelopes.AddChild(volumeColumn);

        var pitchColumn = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        pitchColumn.AddChild(new Label { Text = "Pitch · 32 slices · C4 = 32" });
        _pitchEnvelope.Minimum = 0;
        _pitchEnvelope.Maximum = MaximumPitch;
        _pitchEnvelope.CustomMinimumSize = new Vector2(320f, 120f);
        _pitchEnvelope.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _pitchEnvelope.ValueEdited += (index, value) =>
        {
            _pitchValues[index] = value;
            MarkDraftChanged();
        };
        pitchColumn.AddChild(_pitchEnvelope);
        envelopes.AddChild(pitchColumn);
        _synthControls.AddChild(envelopes);
        editor.AddChild(_synthControls);

        _audioInfo.Visible = false;
        editor.AddChild(_audioInfo);
        _replaceAudioButton.Pressed += ReplaceAudio;
        _replaceAudioButton.Visible = false;
        editor.AddChild(_replaceAudioButton);

        var actions = new HBoxContainer();
        _previewButton.Pressed += PreviewSound;
        _saveButton.Pressed += () => SaveDraft();
        actions.AddChild(_previewButton);
        actions.AddChild(_saveButton);
        actions.AddChild(_status);
        editor.AddChild(actions);
        UpdateButtonState();
    }


    private Button _newButton = null!;
    private Label _speedRowLabel = null!;

    private static void AddEditorRow(VBoxContainer layout, string label, Control editor)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(140f, 0f) });
        editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(editor);
        layout.AddChild(row);
    }

    private List<JsonObject> GetSoundEffects()
    {
        var sounds = new List<JsonObject>();
        JsonArray? sojos = _readSoundEffects();
        if (sojos is null)
            return sounds;
        foreach (JsonNode? node in sojos)
        {
            if (node is JsonObject sojo && LegacySaveDocument.ReadString(sojo["contentType"]) == "SoundEffect")
                sounds.Add(sojo);
        }
        sounds.Sort(static (left, right) =>
        {
            string leftName = LegacySaveDocument.ReadString(left["name"]);
            string rightName = LegacySaveDocument.ReadString(right["name"]);
            int nameOrder = StringComparer.OrdinalIgnoreCase.Compare(leftName, rightName);
            return nameOrder != 0
                ? nameOrder
                : StringComparer.Ordinal.Compare(LegacySaveDocument.ReadString(left["id"]), LegacySaveDocument.ReadString(right["id"]));
        });
        return sounds;
    }

    private static string GetSoundKindLabel(JsonObject sojo)
    {
        JsonObject? content = TryReadContent(sojo);
        return LegacySaveDocument.ReadInt(content?["effectType"], -1) switch
        {
            0 => "Synth",
            1 => "Workshop",
            2 => "Local audio",
            _ => "Legacy"
        };
    }

    private void SelectSound(long index)
    {
        if (_refreshing)
            return;
        string id = _soundList.GetItemMetadata((int)index).AsString();
        if (!SaveDraft())
        {
            _refreshing = true;
            bool restoredSelection = false;
            for (int item = 0; item < _soundList.ItemCount; item++)
            {
                if (_soundList.GetItemMetadata(item).AsString() != _selectedId)
                    continue;
                _soundList.Select(item);
                restoredSelection = true;
                break;
            }
            if (!restoredSelection)
                _soundList.DeselectAll();
            _refreshing = false;
            return;
        }
        JsonObject? sound = GetSoundEffects().FirstOrDefault(candidate => LegacySaveDocument.ReadString(candidate["id"]) == id);
        if (sound is null)
        {
            RefreshFromDocument();
            return;
        }
        _refreshing = true;
        for (int item = 0; item < _soundList.ItemCount; item++)
        {
            if (_soundList.GetItemMetadata(item).AsString() == id)
            {
                _soundList.Select(item);
                break;
            }
        }
        LoadSound(sound);
        _refreshing = false;
        UpdateButtonState();
    }

    private void LoadSound(JsonObject sojo)
    {
        _selectedId = LegacySaveDocument.ReadString(sojo["id"]);
        _originalSojo = sojo.DeepClone().AsObject();
        _isNew = false;
        JsonObject? content = TryReadContent(sojo);
        _isSynthSound = content is not null
            && LegacySaveDocument.ReadInt(content["effectType"], 0) == 0
            && content["synthParams"] is JsonObject;
        _nameField.Text = LegacySaveDocument.ReadString(sojo["name"], "Sound effect");
        _spatialized.ButtonPressed = ReadSpatialized(sojo);
        _synthControls.Visible = _isSynthSound;
        _audioInfo.Visible = !_isSynthSound;
        _replaceAudioButton.Visible = true;

        if (_isSynthSound && content?["synthParams"] is JsonObject synth)
        {
            _volumeValues = ReadEnvelope(synth["volume"], MaximumVolume);
            _pitchValues = ReadEnvelope(synth["pitch"], MaximumPitch / 2);
            _waveShape.Select(Math.Clamp(LegacySaveDocument.ReadInt(synth["waveShape"], 0), 0, 3));
            _speed.Value = Math.Clamp(LegacySaveDocument.ReadInt(synth["speed"], 8), 0, 16);
            _volumeEnvelope.SetValues(_volumeValues);
            _pitchEnvelope.SetValues(_pitchValues);
            _audioInfo.Text = string.Empty;
        }
        else
        {
            _audioInfo.Text = LegacySaveDocument.ReadInt(content?["effectType"], -1) switch
            {
                1 => $"This effect refers to Steam Workshop item {LegacySaveDocument.ReadString(content?["steamWorkshopId"])}. Workshop downloads are unavailable here; import a local WAV or Ogg file to replace it.",
                2 => "This effect uses a local WAV or Ogg asset. Its audio is included with the project sidecar and can be replaced here.",
                _ => "This legacy sound format cannot be played here. Import a local WAV or Ogg file to replace it."
            };
        }
        _status.Text = string.Empty;
    }

    private void BeginNewSound()
    {
        if (!SaveDraft())
            return;
        _refreshing = true;
        _soundList.DeselectAll();
        _selectedId = Guid.NewGuid().ToString("N");
        _originalSojo = null;
        _isNew = true;
        _isSynthSound = true;
        _synthControls.Visible = true;
        _audioInfo.Visible = false;
        _replaceAudioButton.Visible = false;
        _volumeValues = Enumerable.Repeat(MaximumVolume, SliceCount).ToArray();
        _pitchValues = Enumerable.Repeat(MaximumPitch / 2, SliceCount).ToArray();
        _nameField.Text = "New sound";
        _waveShape.Select(0);
        _speed.Value = 8;
        _spatialized.ButtonPressed = true;
        _volumeEnvelope.SetValues(_volumeValues);
        _pitchEnvelope.SetValues(_pitchValues);
        _status.Text = "New synthesized sound · save to add it to the project.";
        _refreshing = false;
        UpdateButtonState();
    }

    private void ImportNewAudio()
    {
        if (SaveDraft())
            _requestAudioImport(null);
    }

    private void ReplaceAudio()
    {
        if (_selectedId is not null && !_isNew && SaveDraft())
            _requestAudioImport(_selectedId);
    }

    private void SetEmptyDraft()
    {
        _isSynthSound = true;
        _synthControls.Visible = true;
        _audioInfo.Visible = false;
        _replaceAudioButton.Visible = false;
        _volumeValues = Enumerable.Repeat(MaximumVolume, SliceCount).ToArray();
        _pitchValues = Enumerable.Repeat(MaximumPitch / 2, SliceCount).ToArray();
        _nameField.Text = string.Empty;
        _waveShape.Select(0);
        _speed.Value = 8;
        _spatialized.ButtonPressed = true;
        _volumeEnvelope.SetValues(_volumeValues);
        _pitchEnvelope.SetValues(_pitchValues);
        _status.Text = "Create a synthesized sound or import local audio.";
    }

    private bool SaveDraft()
    {
        if (_selectedId is null || (!_isNew && _originalSojo is null))
            return true;
        string name = _nameField.Text.Trim();
        if (name.Length == 0)
        {
            _status.Text = "Enter a name before saving.";
            return false;
        }

        JsonObject after = BuildSojo(name);
        if (_originalSojo is not null
            && string.Equals(_originalSojo.ToJsonString(), after.ToJsonString(), StringComparison.Ordinal))
        {
            _status.Text = "No changes to save.";
            return true;
        }

        JsonObject? before = _originalSojo?.DeepClone().AsObject();
        string label = _isNew ? $"Add sound effect {name}" : $"Change sound effect {name}";
        _originalSojo = after.DeepClone().AsObject();
        _isNew = false;
        _saveSoundEffect(before, after, label);
        _reportStatus($"Saved sound effect '{name}'.");
        RefreshFromDocument();
        return true;
    }

    private JsonObject BuildSojo(string name)
    {
        JsonObject sojo = _originalSojo?.DeepClone().AsObject() ?? new JsonObject
        {
            ["id"] = _selectedId,
            ["contentType"] = "SoundEffect"
        };
        sojo["name"] = name;
        JsonObject content = TryReadContent(sojo) ?? new JsonObject();
        content["spatialized"] = _spatialized.ButtonPressed;
        if (_isSynthSound)
        {
            content["effectType"] = 0;
            JsonObject synth = content["synthParams"] as JsonObject ?? new JsonObject();
            synth["waveShape"] = (int)_waveShape.GetSelectedId();
            synth["speed"] = (int)_speed.Value;
            synth["volume"] = ToJsonArray(_volumeValues);
            synth["pitch"] = ToJsonArray(_pitchValues);
            content["synthParams"] = synth;
        }
        sojo["content"] = content.ToJsonString();
        return sojo;
    }

    private void RequestDeleteSound()
    {
        if (_selectedId is null || _isNew)
            return;
        if (!SaveDraft())
            return;
        JsonObject? sound = GetSoundEffects().FirstOrDefault(candidate => LegacySaveDocument.ReadString(candidate["id"]) == _selectedId);
        if (sound is null)
            return;
        _deleteConfirmation.DialogText = $"Delete '{LegacySaveDocument.ReadString(sound["name"])}'? You can undo this from the main toolbar.";
        _deleteConfirmation.PopupCentered(new Vector2I(440, 140));
    }

    private void DeleteCurrentSound()
    {
        if (_selectedId is null)
            return;
        JsonObject? sound = GetSoundEffects().FirstOrDefault(candidate => LegacySaveDocument.ReadString(candidate["id"]) == _selectedId);
        if (sound is null)
            return;
        string name = LegacySaveDocument.ReadString(sound["name"]);
        _saveSoundEffect(sound.DeepClone().AsObject(), null, $"Delete sound effect {name}");
        _selectedId = null;
        _originalSojo = null;
        _isNew = false;
        RefreshFromDocument();
        _reportStatus($"Deleted sound effect '{name}'. Undo is available from the main toolbar.");
    }

    private void PreviewSound()
    {
        if (_selectedId is null)
            return;
        JsonObject sojo = BuildSojo(_nameField.Text.Trim());
        JsonObject? content = TryReadContent(sojo);
        if (content is null)
            return;
        try
        {
            AudioStream? stream;
            if (_isSynthSound && content["synthParams"] is JsonObject synth)
                stream = LegacyBehaviorRuntime.CreateSynthAudioStream(synth);
            else if (LegacySaveDocument.ReadInt(content["effectType"], -1) == 2)
                stream = _loadLocalAudio(content);
            else
            {
                _status.Text = "Import a local WAV or Ogg file to preview this legacy effect.";
                return;
            }
            if (stream is null)
            {
                _status.Text = "The imported audio file is missing or could not be decoded. Replace it with a valid WAV or Ogg file.";
                return;
            }
            _previewPlayer.Stop();
            _previewPlayer.Stream = stream;
            _previewPlayer.Play();
            _status.Text = "Previewing this draft.";
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not preview this sound: {exception.Message}";
        }
    }

    private void MarkDraftChanged()
    {
        if (_refreshing)
            return;
        _status.Text = "Unsaved changes · use Save to apply them to the project.";
    }

    private void UpdateButtonState()
    {
        bool hasDraft = _selectedId is not null;
        _deleteButton.Disabled = !hasDraft || _isNew;
        _previewButton.Disabled = !hasDraft;
        _saveButton.Disabled = !hasDraft;
        _newButton.Disabled = false;
        _importAudioButton.Disabled = false;
        _replaceAudioButton.Visible = hasDraft && !_isNew;
    }

    private void CloseEditor()
    {
        if (!SaveDraft())
            return;
        _previewPlayer.Stop();
        Hide();
    }

    private void StopPreviewWhenHidden()
    {
        if (!Visible)
            _previewPlayer.Stop();
    }

    private static bool TryReadSynthParams(JsonObject sojo, out JsonObject? synth)
    {
        JsonObject? content = TryReadContent(sojo);
        if (content is not null
            && LegacySaveDocument.ReadInt(content["effectType"], 0) == 0
            && content["synthParams"] is JsonObject parameters)
        {
            synth = parameters;
            return true;
        }
        synth = null;
        return false;
    }

    private static JsonObject? TryReadContent(JsonObject sojo)
    {
        string content = LegacySaveDocument.ReadString(sojo["content"]);
        if (content.Length == 0)
            return null;
        try
        {
            return JsonNode.Parse(content) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int[] ReadEnvelope(JsonNode? node, int fallback)
    {
        var values = Enumerable.Repeat(fallback, SliceCount).ToArray();
        if (node is not JsonArray array)
            return values;
        for (int index = 0; index < Math.Min(array.Count, SliceCount); index++)
        {
            try
            {
                values[index] = LegacySaveDocument.ReadInt(array[index], fallback);
            }
            catch (InvalidDataException)
            {
                values[index] = fallback;
            }
        }
        return values;
    }

    private static JsonArray ToJsonArray(IEnumerable<int> values)
    {
        var array = new JsonArray();
        foreach (int value in values)
            array.Add(value);
        return array;
    }

    private static bool ReadSpatialized(JsonObject sojo)
        => TryReadContent(sojo) is JsonObject content
            ? LegacySaveDocument.ReadBool(content["spatialized"], true)
            : true;
}

public sealed partial class LegacySynthEnvelopeEditor : Control
{
    private const int SliceCount = 32;
    private const float LeftInset = 24f;
    private const float RightInset = 10f;
    private const float TopInset = 12f;
    private const float BottomInset = 18f;
    private readonly int[] _values = new int[SliceCount];
    private bool _dragging;

    public int Minimum { get; set; }
    public int Maximum { get; set; } = 16;
    public event Action<int, int>? ValueEdited;

    public LegacySynthEnvelopeEditor()
    {
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public void SetValues(IReadOnlyList<int> values)
    {
        for (int index = 0; index < SliceCount; index++)
            _values[index] = index < values.Count ? values[index] : Minimum;
        QueueRedraw();
    }

    public override void _Draw()
    {
        Rect2 bounds = new(Vector2.Zero, Size);
        DrawRect(bounds, new Color("#111923"));
        float plotWidth = Math.Max(1f, Size.X - LeftInset - RightInset);
        float plotHeight = Math.Max(1f, Size.Y - TopInset - BottomInset);
        for (int gridline = 0; gridline <= 4; gridline++)
        {
            float y = TopInset + plotHeight * gridline / 4f;
            DrawLine(new Vector2(LeftInset, y), new Vector2(LeftInset + plotWidth, y), new Color("#344556"), 1f);
        }

        Vector2 previous = default;
        for (int index = 0; index < SliceCount; index++)
        {
            float x = LeftInset + plotWidth * index / (SliceCount - 1f);
            float normalized = Mathf.Clamp((_values[index] - Minimum) / (float)Math.Max(1, Maximum - Minimum), 0f, 1f);
            float y = TopInset + plotHeight * (1f - normalized);
            var point = new Vector2(x, y);
            if (index > 0)
                DrawLine(previous, point, new Color("#64c8ff"), 2f, true);
            DrawCircle(point, 3.5f, new Color("#d7f2ff"));
            previous = point;
        }
        DrawRect(new Rect2(LeftInset, TopInset, plotWidth, plotHeight), new Color("#607182"), false, 1f);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
        {
            _dragging = mouseButton.Pressed;
            if (_dragging)
                EditAt(mouseButton.Position);
            AcceptEvent();
            return;
        }
        if (_dragging && @event is InputEventMouseMotion mouseMotion)
        {
            EditAt(mouseMotion.Position);
            AcceptEvent();
        }
    }

    private void EditAt(Vector2 position)
    {
        float plotWidth = Math.Max(1f, Size.X - LeftInset - RightInset);
        float plotHeight = Math.Max(1f, Size.Y - TopInset - BottomInset);
        float fraction = Mathf.Clamp((position.X - LeftInset) / plotWidth, 0f, 1f);
        int index = Math.Clamp(Mathf.RoundToInt(fraction * (SliceCount - 1)), 0, SliceCount - 1);
        float valueFraction = 1f - Mathf.Clamp((position.Y - TopInset) / plotHeight, 0f, 1f);
        int value = Mathf.RoundToInt(Minimum + valueFraction * (Maximum - Minimum));
        SetSliceValue(index, value);
    }

    internal void SetSliceValue(int index, int value)
    {
        if ((uint)index >= SliceCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        int clampedValue = Math.Clamp(value, Minimum, Maximum);
        if (_values[index] == clampedValue)
            return;
        _values[index] = clampedValue;
        QueueRedraw();
        ValueEdited?.Invoke(index, clampedValue);
    }
}
