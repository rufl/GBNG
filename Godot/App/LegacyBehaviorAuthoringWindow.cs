using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly LegacyActorData _actor;
    private readonly Action _onChanged;
    private readonly ItemList _assignedUses = new();
    private readonly OptionButton _availableBehaviors = new();
    private readonly TextEdit _scriptEditor = new();
    private readonly TextEdit _assignmentsEditor = new();
    private readonly Button _removeUseButton = new();
    private readonly Button _copyBehaviorButton = new();
    private readonly Button _applyScriptButton = new();
    private readonly Button _applyAssignmentsButton = new();
    private readonly Label _status = new();
    private readonly Dictionary<string, string> _builtinSources = new(StringComparer.Ordinal);
    private bool _refreshing;
    private string _selectedUseId = "";
    private string _selectedUri = "";

    private sealed record BehaviorChoice(string Uri, string Label);

    public LegacyBehaviorAuthoringWindow(LegacySaveDocument document, LegacyActorData actor, Action onChanged)
    {
        _document = document;
        _actor = actor;
        _onChanged = onChanged;
        Title = $"Behavior editor — {actor.DisplayName}";
        Size = new Vector2I(1000, 700);
        CloseRequested += () => QueueFree();
        BuildInterface();
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
        _assignedUses.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _assignedUses.ItemSelected += SelectAssignedUse;
        left.AddChild(_assignedUses);

        _availableBehaviors.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        left.AddChild(_availableBehaviors);
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

        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        columns.AddChild(right);
        var scriptToolbar = new HBoxContainer();
        scriptToolbar.AddChild(new Label { Text = "JavaScript" , SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _copyBehaviorButton.Text = "Create editable copy";
        _copyBehaviorButton.Disabled = true;
        _copyBehaviorButton.Pressed += CopySelectedBehavior;
        scriptToolbar.AddChild(_copyBehaviorButton);
        _applyScriptButton.Text = "Apply code";
        _applyScriptButton.Disabled = true;
        _applyScriptButton.Pressed += ApplyScript;
        scriptToolbar.AddChild(_applyScriptButton);
        right.AddChild(scriptToolbar);
        _scriptEditor.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _scriptEditor.CustomMinimumSize = new Vector2(0f, 250f);
        _scriptEditor.Editable = false;
        right.AddChild(_scriptEditor);

        var assignmentsToolbar = new HBoxContainer();
        assignmentsToolbar.AddChild(new Label
        {
            Text = "Property assignments (legacy JSON)",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        });
        _applyAssignmentsButton.Text = "Apply assignments";
        _applyAssignmentsButton.Disabled = true;
        _applyAssignmentsButton.Pressed += ApplyAssignments;
        assignmentsToolbar.AddChild(_applyAssignmentsButton);
        right.AddChild(assignmentsToolbar);
        _assignmentsEditor.CustomMinimumSize = new Vector2(0f, 130f);
        _assignmentsEditor.Editable = false;
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
        JsonObject? use = GetSelectedUse();
        _selectedUri = use is null ? "" : LegacySaveDocument.ReadString(use["behaviorUri"]);
        _removeUseButton.Disabled = use is null;
        bool hasSource = (_selectedUri.StartsWith("embedded:", StringComparison.Ordinal)
                          && FindEmbeddedBehavior(_selectedUri["embedded:".Length..]) is not null)
                         || (_selectedUri.StartsWith("builtin:", StringComparison.Ordinal)
                             && _builtinSources.ContainsKey(_selectedUri));
        _copyBehaviorButton.Disabled = use is null || !hasSource;
        _applyAssignmentsButton.Disabled = use is null;
        _assignmentsEditor.Editable = use is not null;
        _assignmentsEditor.Text = use?["propertyAssignments"] is JsonArray assignments
            ? assignments.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            : "[]";

        string source = "";
        bool editable = false;
        if (_selectedUri.StartsWith("embedded:", StringComparison.Ordinal)
            && FindEmbeddedBehavior(_selectedUri["embedded:".Length..]) is JsonObject behavior)
        {
            source = LegacySaveDocument.ReadString(behavior["javascript"]);
            editable = true;
        }
        else if (_selectedUri.StartsWith("builtin:", StringComparison.Ordinal)
                 && _builtinSources.TryGetValue(_selectedUri, out string? path))
        {
            source = Godot.FileAccess.GetFileAsString(path);
        }
        _scriptEditor.Text = source;
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
        behavior["javascript"] = _scriptEditor.Text;
        behavior["draftJavascript"] = "";
        NotifyChanged("Applied JavaScript. Save the project to persist it.");
        RefreshAvailableBehaviors();
        RefreshAssignedUses();
    }

    private void ApplyAssignments()
    {
        JsonObject? use = GetSelectedUse();
        if (use is null)
            return;
        try
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
            JsonObject writableUse = FindSelectedUse(EnsureWritableBrain())
                ?? throw new InvalidDataException("The selected behavior use no longer exists.");
            writableUse["propertyAssignments"] = assignments;
            NotifyChanged("Applied property assignments. Save the project to persist them.");
            RefreshAssignedUses();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            _status.Text = exception.Message;
        }
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

    private string GetSelectedMetadataJson()
    {
        if (_selectedUri.StartsWith("embedded:", StringComparison.Ordinal)
            && FindEmbeddedBehavior(_selectedUri["embedded:".Length..]) is JsonObject embedded)
            return LegacySaveDocument.ReadString(embedded["metadataJson"]);
        if (_selectedUri.StartsWith("builtin:", StringComparison.Ordinal)
            && _builtinSources.TryGetValue(_selectedUri, out string? path))
        {
            string metadataPath = path + ".metaJson";
            if (Godot.FileAccess.FileExists(metadataPath))
                return Godot.FileAccess.GetFileAsString(metadataPath);
        }
        return CreateDefaultMetadata(GetBehaviorLabel(_selectedUri));
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
