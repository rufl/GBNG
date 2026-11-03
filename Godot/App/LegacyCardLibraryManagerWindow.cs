using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GBNG.Assets;
using GBNG.Behaviors;
using GBNG.Domain;
using Godot;

namespace GBNG.App;

public sealed partial class LegacyCardLibraryManagerWindow : Window
{
    private const string BuiltinBehaviorRoot = "res://Assets/StreamingAssets/BehaviorLibrary";
    private const string NewCardSource = "// Broadcasts a readable message whenever this actor collides.\nexport function onCollision(msg) {\n  broadcast('ActorCollided', getDisplayName(msg.other));\n}\n";

    private readonly LegacySaveDocument _document;
    private readonly Action _onChanged;
    private readonly LineEdit _search = new();
    private readonly ItemList _cards = new();
    private readonly TextureRect _preview = new();
    private readonly LineEdit _title = new();
    private readonly LineEdit _categories = new();
    private readonly LineEdit _imagePath = new();
    private readonly TextEdit _description = new();
    private readonly TextEdit _javascript = new();
    private readonly Button _copyButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _exportButton = new();
    private readonly Button _applyMetadataButton = new();
    private readonly Button _applyCodeButton = new();
    private readonly Label _status = new();
    private readonly Dictionary<string, JsonObject?> _builtinMetadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _builtinSources = new(StringComparer.Ordinal);
    private readonly FileDialog _importDialog = new();
    private readonly FileDialog _exportDialog = new();
    private readonly ConfirmationDialog _conflictDialog = new();
    private readonly ConfirmationDialog _deleteDialog = new();
    private Dictionary<string, JsonObject>? _pendingImport;
    private bool _refreshing;
    private string _selectedUri = "";

    public LegacyCardLibraryManagerWindow(LegacySaveDocument document, Action onChanged)
    {
        _document = document;
        _onChanged = onChanged;
        Title = "Manage embedded cards";
        Size = new Vector2I(1100, 680);
        CloseRequested += () => QueueFree();
        LoadBuiltinCards();
        BuildInterface();
        RefreshCards();
    }

    public void RefreshFromDocument() => RefreshCards();

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
            Text = "Create and edit project-local cards, copy built-in cards, and import or export legacy card-pack folders. Deletion is disabled while any behavior brain uses the card.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });

        var columns = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        layout.AddChild(columns);
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(330f, 0f) };
        columns.AddChild(left);
        _search.Name = "ManagedCardSearch";
        _search.PlaceholderText = "Search cards by title, category, or description";
        _search.TextChanged += _ => RefreshCards();
        left.AddChild(_search);
        _cards.Name = "ManagedCards";
        _cards.SelectMode = ItemList.SelectModeEnum.Multi;
        _cards.FixedIconSize = new Vector2I(44, 44);
        _cards.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _cards.ItemSelected += OnCardSelected;
        left.AddChild(_cards);

        var buttonsTop = new HBoxContainer();
        var create = new Button { Name = "CreateEmbeddedCard", Text = "New card" };
        create.Pressed += CreateCard;
        buttonsTop.AddChild(create);
        _copyButton.Name = "CopyEmbeddedCard";
        _copyButton.Text = "Copy selected";
        _copyButton.Disabled = true;
        _copyButton.Pressed += CopySelectedCard;
        buttonsTop.AddChild(_copyButton);
        _deleteButton.Name = "DeleteEmbeddedCard";
        _deleteButton.Text = "Delete card";
        _deleteButton.Disabled = true;
        _deleteButton.Pressed += ConfirmDeleteSelectedCard;
        buttonsTop.AddChild(_deleteButton);
        left.AddChild(buttonsTop);

        var buttonsBottom = new HBoxContainer();
        var import = new Button { Name = "ImportCardPack", Text = "Import pack…" };
        import.Pressed += () => _importDialog.PopupCentered(new Vector2I(900, 600));
        buttonsBottom.AddChild(import);
        _exportButton.Name = "ExportCardPack";
        _exportButton.Text = "Export selected…";
        _exportButton.Disabled = true;
        _exportButton.Pressed += () => _exportDialog.PopupCentered(new Vector2I(900, 600));
        buttonsBottom.AddChild(_exportButton);
        left.AddChild(buttonsBottom);

        var rightScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        columns.AddChild(rightScroll);
        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rightScroll.AddChild(right);
        var previewRow = new HBoxContainer();
        _preview.Name = "ManagedCardPreview";
        _preview.CustomMinimumSize = new Vector2(60f, 60f);
        _preview.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _preview.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        previewRow.AddChild(_preview);
        previewRow.AddChild(new Label
        {
            Text = "Built-in cards are read-only. Copy one into this project to edit its code and metadata.",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.Word
        });
        right.AddChild(previewRow);
        right.AddChild(new Label { Text = "Title" });
        _title.Name = "ManagedCardTitle";
        _title.PlaceholderText = "Card title";
        right.AddChild(_title);
        right.AddChild(new Label { Text = "Categories (comma-separated)" });
        _categories.Name = "ManagedCardCategories";
        _categories.PlaceholderText = "Custom";
        right.AddChild(_categories);
        right.AddChild(new Label { Text = "Image resource path" });
        _imagePath.Name = "ManagedCardImagePath";
        _imagePath.PlaceholderText = "BuiltinAssets/CardImages/placeholder";
        _imagePath.TextChanged += imagePath =>
        {
            _preview.Texture = LegacyBehaviorImages.LoadCardIcon(imagePath);
            _preview.Visible = _preview.Texture is not null;
        };
        right.AddChild(_imagePath);
        right.AddChild(new Label { Text = "Description" });
        _description.Name = "ManagedCardDescription";
        _description.CustomMinimumSize = new Vector2(0f, 60f);
        _description.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        right.AddChild(_description);
        _applyMetadataButton.Name = "ApplyManagedCardMetadata";
        _applyMetadataButton.Text = "Apply metadata";
        _applyMetadataButton.Disabled = true;
        _applyMetadataButton.Pressed += ApplyMetadata;
        right.AddChild(_applyMetadataButton);
        var codeHeading = new HBoxContainer();
        codeHeading.AddChild(new Label { Text = "JavaScript", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _applyCodeButton.Name = "ApplyManagedCardCode";
        _applyCodeButton.Text = "Apply code";
        _applyCodeButton.Disabled = true;
        _applyCodeButton.Pressed += ApplyCode;
        codeHeading.AddChild(_applyCodeButton);
        right.AddChild(codeHeading);
        _javascript.Name = "ManagedCardJavaScript";
        _javascript.CustomMinimumSize = new Vector2(0f, 160f);
        _javascript.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        right.AddChild(_javascript);
        _status.Name = "ManagedCardStatus";
        _status.AutowrapMode = TextServer.AutowrapMode.Word;
        layout.AddChild(_status);

        _importDialog.Title = "Import legacy card pack";
        _importDialog.FileMode = FileDialog.FileModeEnum.OpenDir;
        _importDialog.Access = FileDialog.AccessEnum.Filesystem;
        _importDialog.UseNativeDialog = true;
        _importDialog.DirSelected += ImportDirectorySelected;
        AddChild(_importDialog);

        _exportDialog.Title = "Choose export location";
        _exportDialog.FileMode = FileDialog.FileModeEnum.OpenDir;
        _exportDialog.Access = FileDialog.AccessEnum.Filesystem;
        _exportDialog.UseNativeDialog = true;
        _exportDialog.DirSelected += ExportDirectorySelected;
        AddChild(_exportDialog);

        _conflictDialog.Title = "Card pack conflicts";
        _conflictDialog.DialogText = "Some card IDs already exist in this project.";
        _conflictDialog.GetOkButton().Text = "Overwrite";
        _conflictDialog.AddButton("Duplicate", true, "duplicate");
        _conflictDialog.AddButton("Cancel", true, "cancel");
        _conflictDialog.Confirmed += () => CompleteImport(overwrite: true);
        _conflictDialog.CustomAction += action =>
        {
            if (action.ToString() == "duplicate")
                CompleteImport(overwrite: false);
            else
                _pendingImport = null;
        };
        _conflictDialog.Canceled += () => _pendingImport = null;
        AddChild(_conflictDialog);

        _deleteDialog.Title = "Delete embedded card";
        _deleteDialog.GetOkButton().Text = "Delete";
        _deleteDialog.Confirmed += DeleteSelectedCard;
        AddChild(_deleteDialog);
    }

    private void LoadBuiltinCards()
    {
        foreach (string path in LegacyBehaviorRuntime.EnumerateFiles(BuiltinBehaviorRoot, ".js.txt"))
        {
            if (path.Contains("/Specs/", StringComparison.OrdinalIgnoreCase))
                continue;
            string uriName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
            string uri = $"builtin:{uriName}";
            string metadataPath = path + ".metaJson";
            JsonObject? metadata = Godot.FileAccess.FileExists(metadataPath)
                ? ParseMetadata(Godot.FileAccess.GetFileAsString(metadataPath), metadataPath)
                : null;
            if (!IsCardMetadata(metadata))
                continue;
            _builtinMetadata[uri] = metadata;
            _builtinSources[uri] = path;
        }
    }

    private void RefreshCards()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            if (_cards.GetSelectedItems().Length > 0)
                _selectedUri = _cards.GetItemMetadata(_cards.GetSelectedItems()[0]).AsString();
            string filter = _search.Text.Trim();
            _cards.Clear();
            var entries = new List<(string Uri, string Title, JsonObject? Metadata)>();
            foreach ((string uri, JsonObject? metadata) in _builtinMetadata)
                entries.Add((uri, GetCardTitle(uri, metadata), metadata));
            if (TryGetBehaviorArrays(out JsonArray ids, out JsonArray behaviors))
            {
                for (int index = 0; index < ids.Count; index++)
                {
                    string id = LegacySaveDocument.ReadString(ids[index]);
                    if (id.Length == 0 || behaviors[index] is not JsonObject behavior)
                        continue;
                    string uri = $"embedded:{id}";
                    JsonObject? metadata = ParseBehaviorMetadata(behavior, uri);
                    if (!IsCardMetadata(metadata) && metadata is not null)
                        continue;
                    entries.Add((uri, GetCardTitle(uri, metadata, behavior), metadata));
                }
            }

            int selectedIndex = -1;
            foreach ((string uri, string title, JsonObject? metadata) in entries
                .OrderBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase))
            {
                JsonObject? cardData = metadata?["cardSystemCardData"] as JsonObject;
                string description = LegacySaveDocument.ReadString(cardData?["description"]);
                string categories = cardData?["categories"] is JsonArray categoryValues
                    ? string.Join(", ", categoryValues.Select(value => LegacySaveDocument.ReadString(value)))
                    : "";
                if (filter.Length > 0
                    && !title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !description.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !categories.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                Texture2D? icon = LegacyBehaviorImages.LoadCardIcon(LegacySaveDocument.ReadString(cardData?["imageResourcePath"]));
                _cards.AddItem(title, icon);
                int itemIndex = _cards.ItemCount - 1;
                _cards.SetItemMetadata(itemIndex, uri);
                if (uri == _selectedUri)
                    selectedIndex = itemIndex;
            }
            if (selectedIndex >= 0)
                _cards.Select(selectedIndex);
            else if (_cards.ItemCount > 0)
            {
                _cards.Select(0);
                _selectedUri = _cards.GetItemMetadata(0).AsString();
            }
            else
            {
                _selectedUri = "";
            }
        }
        finally
        {
            _refreshing = false;
        }
        if (_cards.ItemCount > 0 && _cards.GetSelectedItems().Length > 0)
            OnCardSelected(_cards.GetSelectedItems()[0]);
        else
            ClearEditor();
    }

    private void OnCardSelected(long index)
    {
        if (_refreshing || index < 0 || index >= _cards.ItemCount)
            return;
        _selectedUri = _cards.GetItemMetadata((int)index).AsString();
        JsonObject? metadata = GetMetadata(_selectedUri);
        JsonObject? cardData = metadata?["cardSystemCardData"] as JsonObject;
        JsonObject? behavior = GetEmbeddedBehavior(_selectedUri);
        bool editable = behavior is not null;
        _preview.Texture = LegacyBehaviorImages.LoadCardIcon(LegacySaveDocument.ReadString(cardData?["imageResourcePath"]));
        _preview.Visible = _preview.Texture is not null;
        _title.Text = LegacySaveDocument.ReadString(cardData?["title"], GetCardTitle(_selectedUri, metadata, behavior));
        _categories.Text = cardData?["categories"] is JsonArray categories
            ? string.Join(", ", categories.Select(value => LegacySaveDocument.ReadString(value)))
            : "";
        _imagePath.Text = LegacySaveDocument.ReadString(cardData?["imageResourcePath"]);
        _description.Text = LegacySaveDocument.ReadString(cardData?["description"]);
        _javascript.Text = behavior is null
            ? GetBuiltinSource(_selectedUri)
            : LegacySaveDocument.ReadString(behavior["javascript"]);
        _title.Editable = editable;
        _categories.Editable = editable;
        _imagePath.Editable = editable;
        _description.Editable = editable;
        _javascript.Editable = editable;
        _applyMetadataButton.Disabled = !editable;
        _applyCodeButton.Disabled = !editable;
        _copyButton.Disabled = GetSource(_selectedUri) is null;
        _deleteButton.Disabled = !editable || HasBehaviorUses(_selectedUri);
        _exportButton.Disabled = GetSelectedEmbeddedIds().Count == 0;
        _status.Text = editable
            ? "Embedded card. Metadata and JavaScript changes apply to this project in memory; use Save in GBNG to persist them."
            : "Built-in card. Copy it to create an editable embedded version.";
    }

    private void ClearEditor()
    {
        _preview.Texture = null;
        _preview.Visible = false;
        _title.Text = "";
        _categories.Text = "";
        _imagePath.Text = "";
        _description.Text = "";
        _javascript.Text = "";
        _title.Editable = false;
        _categories.Editable = false;
        _imagePath.Editable = false;
        _description.Editable = false;
        _javascript.Editable = false;
        _copyButton.Disabled = true;
        _deleteButton.Disabled = true;
        _exportButton.Disabled = true;
        _applyMetadataButton.Disabled = true;
        _applyCodeButton.Disabled = true;
    }

    private void CreateCard()
    {
        try
        {
            JsonObject metadata = CreateDefaultCardMetadata("New custom card");
            string id = NewId(GetExistingBehaviorIds());
            AddEmbeddedBehavior(id, "New custom card", NewCardSource, metadata.ToJsonString());
            _selectedUri = $"embedded:{id}";
            _onChanged();
            RefreshCards();
            _status.Text = "Created an embedded Custom card. Use Save in GBNG to persist it.";
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            _status.Text = exception.Message;
        }
    }

    private void CopySelectedCard()
    {
        if (_selectedUri.Length == 0 || GetSource(_selectedUri) is not string source)
            return;
        try
        {
            JsonObject? sourceMetadata = GetMetadata(_selectedUri);
            JsonObject metadata = sourceMetadata is null
                ? CreateDefaultCardMetadata($"Copy of {GetCardTitle(_selectedUri, sourceMetadata, GetEmbeddedBehavior(_selectedUri))}")
                : sourceMetadata.DeepClone().AsObject();
            string title = GetCardTitle(_selectedUri, sourceMetadata, GetEmbeddedBehavior(_selectedUri));
            string copiedTitle = $"Copy of {title}";
            if (metadata["cardSystemCardData"] is JsonObject cardData)
                cardData["title"] = copiedTitle;
            string id = NewId(GetExistingBehaviorIds());
            AddEmbeddedBehavior(id, copiedTitle, source, metadata.ToJsonString());
            _selectedUri = $"embedded:{id}";
            _onChanged();
            RefreshCards();
            _status.Text = "Created an editable embedded copy. Use Save in GBNG to persist it.";
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            _status.Text = exception.Message;
        }
    }

    private void ApplyMetadata()
    {
        if (GetEmbeddedBehavior(_selectedUri) is not JsonObject behavior)
            return;
        try
        {
            JsonObject metadata = ParseMetadata(LegacySaveDocument.ReadString(behavior["metadataJson"]), $"metadata for '{_selectedUri}'")
                ?? new JsonObject();
            if (metadata["cardSystemPanelData"] is JsonObject panelData && LegacySaveDocument.ReadBool(panelData["isPanel"]))
                throw new InvalidDataException("Panel metadata cannot be edited as card metadata.");
            if (metadata["cardSystemCardData"] is not JsonObject cardData)
            {
                cardData = new JsonObject { ["isCard"] = true };
                metadata["cardSystemCardData"] = cardData;
            }
            cardData["isCard"] = true;
            cardData["title"] = _title.Text;
            cardData["description"] = _description.Text;
            cardData["imageResourcePath"] = _imagePath.Text;
            var categories = new JsonArray();
            var seenCategories = new HashSet<string>(StringComparer.Ordinal);
            foreach (string category in _categories.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (seenCategories.Add(category))
                    categories.Add(category);
            }
            cardData["categories"] = categories;
            behavior["label"] = _title.Text;
            behavior["metadataJson"] = metadata.ToJsonString();
            _onChanged();
            RefreshCards();
            _status.Text = "Updated card metadata in memory. Use Save in GBNG to persist it.";
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            _status.Text = exception.Message;
        }
    }

    private void ApplyCode()
    {
        if (GetEmbeddedBehavior(_selectedUri) is not JsonObject behavior)
            return;
        behavior["javascript"] = _javascript.Text;
        behavior["draftJavascript"] = "";
        _onChanged();
        RefreshCards();
        _status.Text = "Updated embedded card JavaScript in memory. Use Save in GBNG to persist it.";
    }

    private void ConfirmDeleteSelectedCard()
    {
        if (GetEmbeddedBehavior(_selectedUri) is not JsonObject behavior)
            return;
        string title = GetCardTitle(_selectedUri, GetMetadata(_selectedUri), behavior);
        if (HasBehaviorUses(_selectedUri))
        {
            _status.Text = $"Cannot delete '{title}': an actor behavior brain still uses this card.";
            return;
        }
        _deleteDialog.DialogText = $"Delete '{title}' from the embedded card library? This cannot be undone.";
        _deleteDialog.PopupCentered(new Vector2I(520, 180));
    }

    private void DeleteSelectedCard()
    {
        if (!TryGetBehaviorArrays(out JsonArray ids, out JsonArray behaviors)
            || !_selectedUri.StartsWith("embedded:", StringComparison.Ordinal))
            return;
        string id = _selectedUri["embedded:".Length..];
        int index = FindId(ids, id);
        if (index < 0 || HasBehaviorUses(_selectedUri))
        {
            _status.Text = "The card no longer exists or became referenced; it was not deleted.";
            RefreshCards();
            return;
        }
        ids.RemoveAt(index);
        behaviors.RemoveAt(index);
        _selectedUri = "";
        _onChanged();
        RefreshCards();
        _status.Text = "Deleted the unused embedded card. Use Save in GBNG to persist the project.";
    }

    private void ImportDirectorySelected(string directory)
    {
        try
        {
            Dictionary<string, JsonObject> imported = ReadCardPack(directory);
            if (imported.Count == 0)
            {
                _status.Text = "No legacy cards were found in the selected folder.";
                return;
            }
            if (!TryGetBehaviorArrays(out JsonArray ids, out _))
                throw new InvalidDataException("The .voos behaviorIds and behaviors tables are invalid.");
            var existing = ids.Select(node => LegacySaveDocument.ReadString(node)).ToHashSet(StringComparer.Ordinal);
            bool hasConflict = imported.Keys.Any(existing.Contains);
            if (hasConflict)
            {
                _pendingImport = imported;
                _conflictDialog.DialogText = $"{imported.Count} card(s) found; one or more IDs already exist. Overwrite keeps conflicting IDs, Duplicate gives conflicting cards fresh IDs, and Cancel imports nothing.";
                _conflictDialog.PopupCentered(new Vector2I(620, 220));
                return;
            }
            _pendingImport = imported;
            CompleteImport(overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            _status.Text = $"Card pack import failed: {exception.Message}";
        }
    }

    private void CompleteImport(bool overwrite)
    {
        if (_pendingImport is not Dictionary<string, JsonObject> imported)
            return;
        try
        {
            JsonObject database = EnsureBehaviorDatabase();
            if (!TryEnsureBehaviorArrays(database, out JsonArray ids, out JsonArray behaviors))
                throw new InvalidDataException("The .voos behaviorIds and behaviors tables have different lengths.");
            var existing = ids.Select(node => LegacySaveDocument.ReadString(node)).ToHashSet(StringComparer.Ordinal);
            int conflicts = imported.Keys.Count(existing.Contains);
            string selectedId = "";
            foreach ((string incomingId, JsonObject incomingBehavior) in imported.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string targetId = incomingId;
                if (existing.Contains(incomingId))
                {
                    if (overwrite)
                    {
                        int index = FindId(ids, incomingId);
                        if (index < 0)
                            throw new InvalidDataException($"Conflicting card '{incomingId}' disappeared during import.");
                        behaviors[index] = incomingBehavior.DeepClone();
                    }
                    else
                    {
                        targetId = NewId(existing);
                        ids.Add(targetId);
                        behaviors.Add(incomingBehavior.DeepClone());
                        existing.Add(targetId);
                    }
                }
                else
                {
                    ids.Add(targetId);
                    behaviors.Add(incomingBehavior.DeepClone());
                    existing.Add(targetId);
                }
                if (selectedId.Length == 0)
                    selectedId = targetId;
            }
            _pendingImport = null;
            _selectedUri = $"embedded:{selectedId}";
            _onChanged();
            RefreshCards();
            string resolution = conflicts == 0 ? "added" : overwrite ? "overwrote conflicts" : "duplicated conflicts";
            _status.Text = $"Imported {imported.Count} card(s); {resolution}. Use Save in GBNG to persist the project.";
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            _pendingImport = null;
            _status.Text = $"Card pack import failed: {exception.Message}";
        }
    }

    private void ExportDirectorySelected(string directory)
    {
        try
        {
            List<string> ids = GetSelectedEmbeddedIds();
            if (ids.Count == 0)
            {
                _status.Text = "Select one or more embedded cards to export.";
                return;
            }
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("The selected export folder does not exist.");
            string packDirectory = CreateUniqueDirectory(directory, $"cardPack_{DateTime.Now:yyyyMMddTHHmmss}");
            try
            {
                foreach (string id in ids)
                {
                    if (id.Length != 32
                        || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                        || id.Contains('/')
                        || id.Contains('\\'))
                        throw new InvalidDataException($"Embedded card ID '{id}' cannot be represented safely in the legacy card-pack format.");
                    JsonObject behavior = GetEmbeddedBehavior($"embedded:{id}")
                        ?? throw new InvalidDataException($"Embedded card '{id}' no longer exists.");
                    string metadataJson = LegacySaveDocument.ReadString(behavior["metadataJson"]);
                    JsonObject? metadata = ParseMetadata(metadataJson, $"metadata for '{id}'");
                    string title = GetCardTitle($"embedded:{id}", metadata, behavior);
                    string cardDirectory = CreateUniqueDirectory(packDirectory, title);
                    File.WriteAllText(Path.Combine(cardDirectory, $"{id}.js"), LegacySaveDocument.ReadString(behavior["javascript"]));
                    if (metadataJson.Length > 0)
                        File.WriteAllText(Path.Combine(cardDirectory, $"{id}.json"), metadataJson);
                }
            }
            catch
            {
                Directory.Delete(packDirectory, recursive: true);
                throw;
            }
            _status.Text = $"Exported {ids.Count} card(s) to '{packDirectory}'.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            _status.Text = $"Card pack export failed: {exception.Message}";
        }
    }

    private List<string> GetSelectedEmbeddedIds()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (int index in _cards.GetSelectedItems())
        {
            string uri = _cards.GetItemMetadata(index).AsString();
            if (uri.StartsWith("embedded:", StringComparison.Ordinal))
            {
                string id = uri["embedded:".Length..];
                if (id.Length > 0 && seen.Add(id))
                    result.Add(id);
            }
        }
        return result;
    }

    private static Dictionary<string, JsonObject> ReadCardPack(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
            throw new DirectoryNotFoundException($"Card pack folder '{rootDirectory}' does not exist.");
        var imported = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        AddPackDirectory(rootDirectory, imported);
        foreach (string directory in Directory.EnumerateDirectories(rootDirectory, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            AddPackDirectory(directory, imported);
        return imported;
    }

    private static void AddPackDirectory(string directory, Dictionary<string, JsonObject> imported)
    {
        if (!TryReadPackBehavior(directory, out string id, out JsonObject? behavior))
            return;
        if (behavior is null)
            throw new InvalidDataException($"Card pack behavior '{id}' has no readable data.");
        if (!imported.TryAdd(id, behavior))
            throw new InvalidDataException($"Card pack contains the same behavior ID '{id}' more than once.");
    }

    private static bool TryReadPackBehavior(string directory, out string id, out JsonObject? behavior)
    {
        id = "";
        behavior = null;
        string directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        string sourcePath;
        string metadataPath;
        if (directoryName.Length == 32 && File.Exists(Path.Combine(directory, "source.js")))
        {
            id = directoryName;
            sourcePath = Path.Combine(directory, "source.js");
            metadataPath = Path.Combine(directory, "metadata.json");
        }
        else
        {
            string[] sourceFiles = Directory.EnumerateFiles(directory, "*.js", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (sourceFiles.Length != 1)
                return false;
            string candidateId = Path.GetFileNameWithoutExtension(sourceFiles[0]);
            if (candidateId.Length != 32)
                return false;
            id = candidateId;
            sourcePath = sourceFiles[0];
            metadataPath = Path.Combine(directory, $"{id}.json");
        }

        string metadataJson = File.Exists(metadataPath) ? File.ReadAllText(metadataPath) : "";
        _ = ParseMetadata(metadataJson, metadataPath);
        string title = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        if (metadataJson.Length > 0
            && JsonNode.Parse(metadataJson) is JsonObject metadata
            && metadata["cardSystemCardData"] is JsonObject cardData)
            title = LegacySaveDocument.ReadString(cardData["title"], title);
        behavior = new JsonObject
        {
            ["label"] = title,
            ["javascript"] = File.ReadAllText(sourcePath),
            ["draftJavascript"] = "",
            ["userLibraryFile"] = "",
            ["metadataJson"] = metadataJson
        };
        return true;
    }

    private JsonObject? GetMetadata(string uri)
    {
        if (_builtinMetadata.TryGetValue(uri, out JsonObject? builtinMetadata))
            return builtinMetadata;
        return GetEmbeddedBehavior(uri) is JsonObject behavior
            ? ParseBehaviorMetadata(behavior, uri)
            : null;
    }

    private JsonObject? GetEmbeddedBehavior(string uri)
    {
        if (!uri.StartsWith("embedded:", StringComparison.Ordinal)
            || !TryGetBehaviorArrays(_document.BehaviorDatabase, out JsonArray ids, out JsonArray behaviors))
            return null;
        int index = FindId(ids, uri["embedded:".Length..]);
        return index >= 0 ? behaviors[index] as JsonObject : null;
    }

    private string? GetSource(string uri)
    {
        if (uri.StartsWith("builtin:", StringComparison.Ordinal))
            return GetBuiltinSource(uri);
        return GetEmbeddedBehavior(uri) is JsonObject behavior
            ? LegacySaveDocument.ReadString(behavior["javascript"])
            : null;
    }

    private string GetBuiltinSource(string uri)
        => _builtinSources.TryGetValue(uri, out string? path) ? Godot.FileAccess.GetFileAsString(path) : "";

    private bool HasBehaviorUses(string uri)
    {
        JsonObject? database = _document.BehaviorDatabase;
        if (database?["brains"] is not JsonArray brains)
            return false;
        foreach (JsonObject brain in brains.OfType<JsonObject>())
        {
            if (brain["behaviorUses"] is JsonArray uses
                && uses.OfType<JsonObject>().Any(use => LegacySaveDocument.ReadString(use["behaviorUri"]) == uri))
                return true;
        }
        return false;
    }

    private static bool IsCardMetadata(JsonObject? metadata)
    {
        if (metadata is null)
            return false;
        JsonObject? cardData = metadata["cardSystemCardData"] as JsonObject;
        return cardData is not null && LegacySaveDocument.ReadBool(cardData["isCard"]);
    }

    private static string GetCardTitle(string uri, JsonObject? metadata, JsonObject? behavior = null)
    {
        string title = LegacySaveDocument.ReadString((metadata?["cardSystemCardData"] as JsonObject)?["title"]);
        if (title.Length == 0)
            title = LegacySaveDocument.ReadString(behavior?["label"]);
        if (title.Length > 0)
            return title;
        if (uri.StartsWith("builtin:", StringComparison.Ordinal))
            return uri["builtin:".Length..];
        if (uri.StartsWith("embedded:", StringComparison.Ordinal))
        {
            string id = uri["embedded:".Length..];
            return $"Embedded · {id[..Math.Min(8, id.Length)]}";
        }
        return uri;
    }

    private static JsonObject? ParseBehaviorMetadata(JsonObject behavior, string context)
        => ParseMetadata(LegacySaveDocument.ReadString(behavior["metadataJson"]), context);

    private static JsonObject? ParseMetadata(string encoded, string context)
    {
        if (encoded.Length == 0)
            return null;
        if (JsonNode.Parse(encoded) is not JsonObject metadata)
            throw new InvalidDataException($"Metadata for '{context}' must be a JSON object.");
        return metadata;
    }

    private static JsonObject CreateDefaultCardMetadata(string title)
    {
        var categories = new JsonArray { "Custom" };
        return new JsonObject
        {
            ["cardSystemCardData"] = new JsonObject
            {
                ["isCard"] = true,
                ["title"] = title,
                ["description"] = "A custom Game Builder behavior.",
                ["categories"] = categories,
                ["imageResourcePath"] = "",
                ["listPriority"] = 0,
                ["hidden"] = false
            }
        };
    }

    private List<string> GetExistingBehaviorIds()
    {
        if (!TryGetBehaviorArrays(_document.BehaviorDatabase, out JsonArray ids, out _))
            return new List<string>();
        return ids.Select(node => LegacySaveDocument.ReadString(node)).ToList();
    }

    private void AddEmbeddedBehavior(string id, string label, string source, string metadataJson)
    {
        JsonObject database = EnsureBehaviorDatabase();
        if (!TryEnsureBehaviorArrays(database, out JsonArray ids, out JsonArray behaviors))
            throw new InvalidDataException("The .voos behaviorIds and behaviors tables have different lengths.");
        if (FindId(ids, id) >= 0)
            throw new InvalidDataException($"Embedded card '{id}' already exists.");
        ids.Add(id);
        behaviors.Add(new JsonObject
        {
            ["label"] = label,
            ["javascript"] = source,
            ["draftJavascript"] = "",
            ["userLibraryFile"] = "",
            ["metadataJson"] = metadataJson
        });
    }

    private JsonObject EnsureBehaviorDatabase()
    {
        if (_document.Root["behaviorDatabase"] is JsonObject database)
            return database;
        if (_document.Root["behaviorDatabase"] is not null)
            throw new InvalidDataException("The .voos behaviorDatabase field is not an object.");
        database = new JsonObject
        {
            ["version"] = 4,
            ["behaviorIds"] = new JsonArray(),
            ["behaviors"] = new JsonArray(),
            ["brainIds"] = new JsonArray(),
            ["brains"] = new JsonArray()
        };
        _document.Root["behaviorDatabase"] = database;
        return database;
    }

    private bool TryGetBehaviorArrays(out JsonArray ids, out JsonArray behaviors)
        => TryGetBehaviorArrays(_document.BehaviorDatabase, out ids, out behaviors);

    private static bool TryGetBehaviorArrays(JsonObject? database, out JsonArray ids, out JsonArray behaviors)
    {
        ids = database?["behaviorIds"] as JsonArray ?? new JsonArray();
        behaviors = database?["behaviors"] as JsonArray ?? new JsonArray();
        return database?["behaviorIds"] is JsonArray && database["behaviors"] is JsonArray && ids.Count == behaviors.Count;
    }

    private static bool TryEnsureBehaviorArrays(JsonObject database, out JsonArray ids, out JsonArray behaviors)
    {
        ids = database["behaviorIds"] as JsonArray ?? new JsonArray();
        behaviors = database["behaviors"] as JsonArray ?? new JsonArray();
        if (database["behaviorIds"] is null)
            database["behaviorIds"] = ids;
        if (database["behaviors"] is null)
            database["behaviors"] = behaviors;
        return database["behaviorIds"] is JsonArray && database["behaviors"] is JsonArray && ids.Count == behaviors.Count;
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

    private static string NewId(IEnumerable<string> existing)
    {
        var ids = existing.ToHashSet(StringComparer.Ordinal);
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        } while (ids.Contains(id));
        return id;
    }

    private static string CreateUniqueDirectory(string parent, string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safeName = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        if (safeName.Length == 0 || safeName is "." or "..")
            safeName = "Card";
        string path = Path.Combine(parent, safeName);
        int suffix = 1;
        while (Directory.Exists(path) || File.Exists(path))
            path = Path.Combine(parent, $"{safeName} {suffix++}");
        Directory.CreateDirectory(path);
        return path;
    }
}
