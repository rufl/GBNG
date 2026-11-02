using System.Text.Json;
using System.Text.Json.Nodes;
using GBNG.Behaviors;
using GBNG.Domain;
using Godot;

namespace GBNG.App;

public sealed partial class LegacyPanelCanvasWindow : Window
{
    private const string BuiltinBehaviorRoot = "res://Assets/StreamingAssets/BehaviorLibrary";
    private const string CustomCategory = "Custom";
    private const string DefaultBrainId = "__DEFAULT_BRAIN__";
    private sealed record CardChoice(string Value, string Label);

    private GraphNode? _customNode;
    private int _canvasBuildGeneration;
    private readonly LegacySaveDocument _document;
    private readonly LegacyActorData _actor;
    private readonly Action _onChanged;
    private readonly Func<string, JsonArray?> _getPropertyDefinitions;
    private readonly Action _createCustomCard;
    private readonly OptionButton _panelPicker = new();
    private readonly GraphEdit _canvas = new();
    private readonly Label _status = new();
    private readonly Dictionary<string, JsonObject?> _builtinMetadata = new(StringComparer.Ordinal);
    private readonly List<(GraphNode Node, JsonObject Use)> _panelNodes = new();
    private readonly List<(GraphNode Node, TextEdit Text, JsonObject Data)> _noteNodes = new();
    private readonly HashSet<GraphNode> _resizeBoundNoteNodes = new();
    private JsonArray _notes = new();
    private int _nextNoteNodeId;
    private JsonObject? _brain;
    private bool _building;
    private bool _viewInitialized;
    private bool _layoutDirty;
    private JsonObject? _pendingViewState;

    public LegacyPanelCanvasWindow(
        LegacySaveDocument document,
        LegacyActorData actor,
        Action onChanged,
        Func<string, JsonArray?> getPropertyDefinitions,
        Action createCustomCard)
    {
        _document = document;
        _actor = actor;
        _onChanged = onChanged;
        _getPropertyDefinitions = getPropertyDefinitions;
        _createCustomCard = createCustomCard;
        Title = $"Behavior panels — {actor.DisplayName}";
        Size = new Vector2I(1100, 760);
        CloseRequested += CloseCanvas;
        LoadBuiltinMetadata();
        BuildInterface();
        PopulatePanelChoices();
        PopulateCanvas();
    }

    public override void _Ready()
    {
        GetTree().CreateTimer(0.1).Timeout += InitializeCanvasView;
    }

    private void InitializeCanvasView()
    {
        if (!IsInsideTree())
            return;
        if (_pendingViewState is JsonObject viewState)
        {
            _canvas.Zoom = Math.Clamp(LegacySaveDocument.ReadFloat(viewState["scale"], 1f), _canvas.ZoomMin, _canvas.ZoomMax);
            _canvas.ScrollOffset = ReadVector2(viewState["viewPosition"], Vector2.Zero);
            _pendingViewState = null;
            _viewInitialized = true;
            return;
        }
        FitCanvasToContent();
        _viewInitialized = true;
    }

    private void FitCanvasToContent()
    {
        GraphNode[] nodes = _canvas.GetChildren().OfType<GraphNode>().ToArray();
        if (nodes.Length == 0 || _canvas.Size.X <= 0f || _canvas.Size.Y <= 0f)
            return;

        Vector2 minimum = nodes[0].PositionOffset;
        Vector2 maximum = minimum + nodes[0].Size;
        foreach (GraphNode node in nodes)
        {
            minimum = minimum.Min(node.PositionOffset);
            maximum = maximum.Max(node.PositionOffset + node.Size);
        }

        Vector2 contentSize = maximum - minimum;
        Vector2 availableSize = _canvas.Size - new Vector2(48f, 96f);
        float fitZoom = Math.Min(
            availableSize.X / Math.Max(contentSize.X, 1f),
            availableSize.Y / Math.Max(contentSize.Y, 1f));
        _canvas.Zoom = Math.Clamp(fitZoom, _canvas.ZoomMin, _canvas.ZoomMax);
        Vector2 viewCenter = new(_canvas.Size.X / 2f, (_canvas.Size.Y + 48f) / 2f);
        Vector2 contentCenter = (minimum + maximum) / 2f;
        _canvas.ScrollOffset = contentCenter * _canvas.Zoom - viewCenter;
    }

    private void ResetCanvasView()
    {
        FitCanvasToContent();
        SaveViewState();
    }

    private void BuildInterface()
    {
        var margin = new MarginContainer { AnchorRight = 1f, AnchorBottom = 1f };
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        AddChild(margin);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 8);
        margin.AddChild(layout);
        var toolbar = new HBoxContainer();
        toolbar.AddChild(new Label
        {
            Text = "Arrange panels and notes, add or move cards between panel decks, and remove card uses here. Edit card properties and scripts in the behavior authoring window.",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.Word
        });
        var addNote = new Button { Text = "Add note" };
        addNote.Pressed += AddPanelNote;
        toolbar.AddChild(addNote);
        var saveView = new Button { Text = "Save view" };
        saveView.Pressed += SaveViewState;
        toolbar.AddChild(saveView);
        var center = new Button { Text = "Reset view" };
        center.Pressed += ResetCanvasView;
        toolbar.AddChild(center);
        layout.AddChild(toolbar);

        var panelLibrary = new HBoxContainer();
        panelLibrary.AddChild(new Label { Text = "Built-in panel:", SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter });
        _panelPicker.Name = "BuiltinPanelPicker";
        _panelPicker.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        panelLibrary.AddChild(_panelPicker);
        var addPanel = new Button { Name = "AddBuiltinPanel", Text = "Add panel" };
        addPanel.Pressed += AddSelectedPanel;
        panelLibrary.AddChild(addPanel);
        layout.AddChild(panelLibrary);

        _canvas.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _canvas.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _canvas.ShowGrid = true;
        _canvas.SnappingEnabled = true;
        _canvas.SnappingDistance = 20;
        _canvas.ShowZoomButtons = true;
        _canvas.ShowMinimapButton = true;
        _canvas.ZoomMin = 0.2f;
        _canvas.ZoomMax = 1.5f;
        _canvas.ZoomStep = 1.2f;
        _canvas.EndNodeMove += SaveNodePositions;
        layout.AddChild(_canvas);
        _status.AutowrapMode = TextServer.AutowrapMode.Word;
        layout.AddChild(_status);
    }

    private void PopulateCanvas()
    {
        JsonObject? database = _document.BehaviorDatabase;
        if (database?["brainIds"] is not JsonArray brainIds || database["brains"] is not JsonArray brains)
        {
            _status.Text = "This actor has no behavior brain to display.";
            return;
        }
        int brainIndex = FindStringIndex(brainIds, _actor.BrainName);
        if (brainIndex < 0 || brainIndex >= brains.Count || brains[brainIndex] is not JsonObject brain)
        {
            _status.Text = "This actor has no behavior brain to display.";
            return;
        }
        _brain = brain;
        _building = true;
        try
        {
            RestoreViewState(brain);
            if (brain["behaviorUses"] is not JsonArray uses)
            {
                _status.Text = "This behavior brain has no assigned uses.";
                return;
            }

            var usesById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (JsonNode? node in uses)
            {
                if (node is not JsonObject use)
                    continue;
                string id = LegacySaveDocument.ReadString(use["id"]);
                if (id.Length > 0)
                    usesById.TryAdd(id, use);
            }

            var assignedCardIds = new HashSet<string>(StringComparer.Ordinal);
            int panelIndex = 0;
            foreach (JsonNode? node in uses)
            {
                if (node is not JsonObject use)
                    continue;
                string uri = LegacySaveDocument.ReadString(use["behaviorUri"]);
                JsonObject? metadata = GetBehaviorMetadata(uri);
                JsonObject? panelData = metadata?["cardSystemPanelData"] as JsonObject;
                if (panelData is null || !LegacySaveDocument.ReadBool(panelData["isPanel"]))
                    continue;

                GraphNode graphNode = CreatePanelNode(use, panelData, usesById, assignedCardIds, panelIndex);
                _canvas.AddChild(graphNode);
                _panelNodes.Add((graphNode, use));
                panelIndex++;
            }

            GraphNode customNode = CreateCustomNode(uses, assignedCardIds, panelIndex);
            _canvas.AddChild(customNode);
            _customNode = customNode;
            JsonObject brainMetadata = ParseObjectString(brain["metadataJson"], "behavior brain metadata");
            _notes = brainMetadata["panelNotes"] as JsonArray ?? new JsonArray();
            int noteIndex = 0;
            foreach (JsonNode? noteData in _notes)
            {
                if (noteData is not JsonObject note)
                    continue;
                GraphNode noteNode = CreateNoteNode(note, noteIndex++);
                _canvas.AddChild(noteNode);
                BindNoteResize(noteNode, note, _noteNodes[^1].Text);
            }
            _status.Text = $"{panelIndex} panel(s), the Custom panel, and {_noteNodes.Count} note(s). Layout changes remain in memory until GBNG saves the project.";
            _nextNoteNodeId = noteIndex;
        }
        finally
        {
            _building = false;
        }
    }

    private GraphNode CreatePanelNode(
        JsonObject use,
        JsonObject panelData,
        IReadOnlyDictionary<string, JsonObject> usesById,
        HashSet<string> assignedCardIds,
        int panelIndex)
    {
        string uri = LegacySaveDocument.ReadString(use["behaviorUri"]);
        JsonObject metadata = ParseObjectString(use["metadataJson"], $"metadata for '{uri}'");
        string title = LegacySaveDocument.ReadString(panelData["title"], uri);
        string customLabel = LegacySaveDocument.ReadString(metadata["customLabel"]);
        var graphNode = new GraphNode
        {
            Name = $"panel_{panelIndex}_{_canvasBuildGeneration}",
            Title = customLabel.Length > 0 ? customLabel : title,
            PositionOffset = ReadVector2(metadata["position"], new Vector2(50 + panelIndex * 380, 60)),
            CustomMinimumSize = new Vector2(310, 0),
            Resizable = true,
            Draggable = true,
            Selectable = true
        };
        Color panelColor = ReadColor(panelData["color"], new Color("#626d78"));
        graphNode.AddThemeColorOverride("title_color", panelColor.Lightened(0.25f));
        graphNode.AddThemeFontSizeOverride("title_font_size", 24);
        graphNode.AddChild(CreateCanvasLabel(LegacySaveDocument.ReadString(panelData["description"])));

        string panelUseId = LegacySaveDocument.ReadString(use["id"]);
        var panelTitle = new LineEdit
        {
            Name = $"PanelTitle_{panelUseId}",
            Text = customLabel,
            PlaceholderText = title,
            TooltipText = "Custom panel title. Leave blank to restore the behavior title.",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var titleRow = new HBoxContainer();
        titleRow.AddChild(panelTitle);
        var applyTitle = new Button { Name = $"ApplyPanelTitle_{panelUseId}", Text = "Apply title" };
        applyTitle.Pressed += () => SavePanelTitle(panelUseId, graphNode, panelTitle, title);
        titleRow.AddChild(applyTitle);
        graphNode.AddChild(titleRow);
        var deletePanel = new Button { Name = $"DeletePanel_{panelUseId}", Text = "Delete panel" };
        deletePanel.Pressed += () => DeletePanel(panelUseId);
        graphNode.AddChild(deletePanel);
        var duplicatePanel = new Button { Name = $"DuplicatePanel_{panelUseId}", Text = "Duplicate panel" };
        duplicatePanel.Pressed += () => DuplicatePanel(panelUseId);
        graphNode.AddChild(duplicatePanel);

        JsonArray? definitions = _getPropertyDefinitions(uri);
        if (definitions is null)
        {
            graphNode.AddChild(CreateCanvasLabel("Card slot definitions are unavailable."));
            return graphNode;
        }

        bool hasDeck = false;
        foreach (JsonNode? definitionNode in definitions)
        {
            if (definitionNode is not JsonObject definition
                || LegacySaveDocument.ReadString(definition["type"]) != "CardDeck")
                continue;
            string propertyName = LegacySaveDocument.ReadString(definition["variableName"]);
            if (propertyName.Length == 0)
                continue;
            hasDeck = true;
            string label = LegacySaveDocument.ReadString(definition["label"], propertyName);
            graphNode.AddChild(new HSeparator());
            graphNode.AddChild(CreateCanvasLabel(label, LegacySaveDocument.ReadString(definition["comment"])));
            JsonNode? assignedValue = ReadAssignedValue(use, propertyName);
            graphNode.AddChild(CreateCardDeckEditor(use, usesById, definition, propertyName, assignedValue));
            if (assignedValue is JsonArray cardIds)
            {
                foreach (JsonNode? cardIdNode in cardIds)
                {
                    string cardId = LegacySaveDocument.ReadString(cardIdNode);
                    if (cardId.Length > 0)
                        assignedCardIds.Add(cardId);
                }
            }
        }
        if (!hasDeck)
            graphNode.AddChild(CreateCanvasLabel("No card decks."));
        return graphNode;
    }

    private Control CreateCardDeckEditor(
        JsonObject panelUse,
        IReadOnlyDictionary<string, JsonObject> usesById,
        JsonObject definition,
        string propertyName,
        JsonNode? value)
    {
        string category = LegacySaveDocument.ReadString((definition["deckOptions"] as JsonObject)?["cardCategory"], "Custom");
        if (category.Length == 0)
            category = CustomCategory;
        bool oneCardOnly = LegacySaveDocument.ReadBool((definition["deckOptions"] as JsonObject)?["oneCardOnly"]);
        JsonArray? assignedCards = value as JsonArray;
        var assignedIds = new HashSet<string>(StringComparer.Ordinal);
        var assignedEntries = new List<(string Id, int Index)>();
        var editor = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        if (assignedCards is not null)
        {
            for (int index = 0; index < assignedCards.Count; index++)
            {
                string cardId = LegacySaveDocument.ReadString(assignedCards[index]);
                if (cardId.Length == 0)
                    continue;
                assignedIds.Add(cardId);
                assignedEntries.Add((cardId, index));
            }
        }
        string panelUseId = LegacySaveDocument.ReadString(panelUse["id"]);
        foreach ((string cardId, int deckIndex) in assignedEntries)
        {
            string title = usesById.TryGetValue(cardId, out JsonObject? cardUse)
                ? GetCardDisplayName(LegacySaveDocument.ReadString(cardUse["behaviorUri"]))
                : $"Missing use · {cardId}";
            var row = new HBoxContainer();
            row.AddChild(new Label
            {
                Text = title,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.Word
            });
            var moveUp = new Button
            {
                Name = $"MoveCardUp_{panelUseId}_{propertyName}_{cardId}",
                Text = "Up",
                TooltipText = "Move this card earlier in the deck.",
                Disabled = deckIndex == assignedEntries[0].Index
            };
            moveUp.Pressed += () => MoveCardInDeck(panelUseId, propertyName, cardId, -1);
            row.AddChild(moveUp);
            var moveDown = new Button
            {
                Name = $"MoveCardDown_{panelUseId}_{propertyName}_{cardId}",
                Text = "Down",
                TooltipText = "Move this card later in the deck.",
                Disabled = deckIndex == assignedEntries[^1].Index
            };
            moveDown.Pressed += () => MoveCardInDeck(panelUseId, propertyName, cardId, 1);
            row.AddChild(moveDown);
            if (usesById.TryGetValue(cardId, out JsonObject? customCardUse)
                && IsCustomPanelCard(LegacySaveDocument.ReadString(customCardUse["behaviorUri"])))
            {
                var returnToCustom = new Button { Name = $"ReturnToCustom_{cardId}", Text = "Return to Custom" };
                returnToCustom.Pressed += () => ReturnCardToCustom(cardId);
                row.AddChild(returnToCustom);
            }
            var remove = new Button { Name = $"RemoveCard_{cardId}", Text = "Remove" };
            remove.Pressed += () => RemoveCardFromDeck(panelUseId, propertyName, cardId);
            row.AddChild(remove);
            editor.AddChild(row);
        }
        if (assignedEntries.Count == 0)
            editor.AddChild(CreateCanvasLabel("(empty)"));

        var picker = new OptionButton
        {
            Name = $"DeckPicker_{LegacySaveDocument.ReadString(panelUse["id"])}_{propertyName}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, 36f)
        };
        picker.AddItem("(choose a card)");
        picker.SetItemMetadata(0, "");
        List<CardChoice> choices = GetCardChoices(category, usesById.Values, assignedIds);
        foreach (CardChoice choice in choices)
        {
            picker.AddItem(choice.Label);
            picker.SetItemMetadata(picker.ItemCount - 1, choice.Value);
        }
        var add = new Button
        {
            Name = $"AddCard_{LegacySaveDocument.ReadString(panelUse["id"])}_{propertyName}",
            Text = oneCardOnly && assignedIds.Count > 0 ? "Replace card" : "Add card",
            Disabled = true
        };
        picker.ItemSelected += index => add.Disabled = picker.GetItemMetadata((int)index).AsString().Length == 0;
        add.Pressed += () =>
        {
            if (picker.Selected < 0)
                return;
            string selected = picker.GetItemMetadata(picker.Selected).AsString();
            if (selected.Length > 0)
                AssignCardToDeck(LegacySaveDocument.ReadString(panelUse["id"]), propertyName, category, selected, oneCardOnly);
        };
        var addRow = new HBoxContainer();
        addRow.AddChild(picker);
        addRow.AddChild(add);
        editor.AddChild(addRow);
        return editor;
    }

    private List<CardChoice> GetCardChoices(string category, IEnumerable<JsonObject> brainUses, HashSet<string> assignedIds)
    {
        var choices = new List<CardChoice>();
        var seenUris = new HashSet<string>(StringComparer.Ordinal);
        foreach (string uri in _builtinMetadata.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (IsCardInCategory(uri, category) && seenUris.Add(uri))
                choices.Add(new CardChoice($"behavior:{uri}", GetCardDisplayName(uri)));
        }

        JsonObject? database = _document.BehaviorDatabase;
        if (database?["behaviorIds"] is JsonArray behaviorIds
            && database["behaviors"] is JsonArray behaviors)
        {
            for (int index = 0; index < behaviorIds.Count && index < behaviors.Count; index++)
            {
                string behaviorId = LegacySaveDocument.ReadString(behaviorIds[index]);
                string uri = $"embedded:{behaviorId}";
                if (behaviorId.Length > 0 && IsCardInCategory(uri, category) && seenUris.Add(uri))
                    choices.Add(new CardChoice($"behavior:{uri}", GetCardDisplayName(uri)));
            }
        }

        foreach (JsonObject use in brainUses)
        {
            string id = LegacySaveDocument.ReadString(use["id"]);
            string uri = LegacySaveDocument.ReadString(use["behaviorUri"]);
            if (id.Length == 0 || id == DefaultBrainId || uri == "builtin:Default Behavior"
                || assignedIds.Contains(id) || !IsCardInCategory(uri, category))
                continue;
            choices.Add(new CardChoice($"use:{id}", $"{GetCardDisplayName(uri)} (move)"));
        }
        return choices;
    }

    private void AssignCardToDeck(string panelUseId, string propertyName, string category, string selected, bool oneCardOnly)
    {
        try
        {
            JsonObject brain = EnsureWritableBrain();
            if (brain["behaviorUses"] is not JsonArray uses)
                throw new InvalidDataException("This behavior brain has no assigned uses.");
            JsonObject panelUse = FindUse(uses, panelUseId)
                ?? throw new InvalidDataException("The selected panel no longer exists.");
            JsonArray currentCards = ReadAssignedValue(panelUse, propertyName) as JsonArray ?? new JsonArray();
            string useId;
            if (selected.StartsWith("use:", StringComparison.Ordinal))
            {
                useId = selected["use:".Length..];
                JsonObject cardUse = FindUse(uses, useId)
                    ?? throw new InvalidDataException("The selected card use no longer exists.");
                if (!IsCardInCategory(LegacySaveDocument.ReadString(cardUse["behaviorUri"]), category))
                    throw new InvalidDataException("The selected card is not valid for this deck.");
            }
            else if (selected.StartsWith("behavior:", StringComparison.Ordinal))
            {
                string uri = selected["behavior:".Length..];
                if (!IsCardInCategory(uri, category))
                    throw new InvalidDataException("The selected card is not valid for this deck.");
                useId = NewUseId(uses);
                uses.Add(new JsonObject
                {
                    ["id"] = useId,
                    ["behaviorUri"] = uri,
                    ["metadataJson"] = "",
                    ["propertyAssignments"] = new JsonArray()
                });
            }
            else
            {
                throw new InvalidDataException("The selected card choice is invalid.");
            }

            if (currentCards.Any(node => LegacySaveDocument.ReadString(node) == useId))
                return;
            if (oneCardOnly)
            {
                foreach (string previousId in currentCards.Select(node => LegacySaveDocument.ReadString(node)).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal))
                {
                    if (previousId == useId)
                        continue;
                    RemoveUseFromDecks(uses, previousId);
                    RemoveBehaviorUse(uses, previousId);
                }
            }
            RemoveUseFromDecks(uses, useId);
            currentCards = ReadAssignedValue(panelUse, propertyName) as JsonArray ?? new JsonArray();
            currentCards.Add(useId);
            WriteAssignedValue(panelUse, propertyName, currentCards);
            ApplyCardDeckChange();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or OverflowException)
        {
            _status.Text = exception.Message;
        }
    }

    private void MoveCardInDeck(string panelUseId, string propertyName, string cardUseId, int offset)
    {
        try
        {
            JsonObject brain = EnsureWritableBrain();
            if (brain["behaviorUses"] is not JsonArray uses)
                throw new InvalidDataException("This behavior brain has no assigned uses.");
            JsonObject panelUse = FindUse(uses, panelUseId)
                ?? throw new InvalidDataException("The selected panel no longer exists.");
            JsonArray cardIds = ReadAssignedValue(panelUse, propertyName) as JsonArray
                ?? throw new InvalidDataException($"Deck '{propertyName}' has no ordered card list.");
            int sourceIndex = -1;
            for (int index = 0; index < cardIds.Count; index++)
            {
                if (LegacySaveDocument.ReadString(cardIds[index]) == cardUseId)
                {
                    sourceIndex = index;
                    break;
                }
            }
            int targetIndex = sourceIndex + offset;
            if (sourceIndex < 0 || targetIndex < 0 || targetIndex >= cardIds.Count)
                return;
            JsonNode? movingCardId = cardIds[sourceIndex]?.DeepClone();
            cardIds.RemoveAt(sourceIndex);
            cardIds.Insert(targetIndex, movingCardId);
            WriteAssignedValue(panelUse, propertyName, cardIds);
            ApplyCardDeckChange();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or OverflowException)
        {
            _status.Text = exception.Message;
        }
    }

    private void RemoveCardFromDeck(string panelUseId, string propertyName, string cardUseId)
    {
        try
        {
            JsonObject brain = EnsureWritableBrain();
            if (brain["behaviorUses"] is not JsonArray uses)
                throw new InvalidDataException("This behavior brain has no assigned uses.");
            JsonObject panelUse = FindUse(uses, panelUseId)
                ?? throw new InvalidDataException("The selected panel no longer exists.");
            RemoveUseFromDecks(uses, cardUseId);
            RemoveBehaviorUse(uses, cardUseId);
            WriteAssignedValue(panelUse, propertyName, ReadAssignedValue(panelUse, propertyName) as JsonArray ?? new JsonArray());
            ApplyCardDeckChange();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or OverflowException)
        {
            _status.Text = exception.Message;
        }
    }

    private void ApplyCardDeckChange()
    {
        _layoutDirty = true;
        _onChanged();
        RebuildCanvas();
        _status.Text = "Card assignment updated in memory. Use Save in GBNG to persist the project.";
    }

    private void SavePanelTitle(string panelUseId, GraphNode panelNode, LineEdit editor, string defaultTitle)
    {
        try
        {
            string customLabel = editor.Text.Trim();
            if (_brain?["behaviorUses"] is JsonArray currentUses
                && FindUse(currentUses, panelUseId) is JsonObject currentUse
                && LegacySaveDocument.ReadString(ParseObjectString(currentUse["metadataJson"], "panel-use metadata")["customLabel"]) == customLabel)
                return;
            JsonObject brain = EnsureWritableBrain();
            if (brain["behaviorUses"] is not JsonArray uses)
                throw new InvalidDataException("This behavior brain has no assigned uses.");
            JsonObject panelUse = FindUse(uses, panelUseId)
                ?? throw new InvalidDataException("The selected panel no longer exists.");
            JsonObject metadata = ParseObjectString(panelUse["metadataJson"], "panel-use metadata");
            metadata["version"] = 1;
            metadata["customLabel"] = customLabel;
            panelUse["metadataJson"] = metadata.ToJsonString();
            editor.Text = customLabel;
            panelNode.Title = customLabel.Length > 0 ? customLabel : defaultTitle;
            _layoutDirty = true;
            _onChanged();
            RebuildCanvas();
            _status.Text = "Panel title updated in memory. Use Save in GBNG to persist the project.";
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            _status.Text = exception.Message;
        }
    }

    private void SaveCustomPanelTitle(GraphNode panelNode, LineEdit editor)
    {
        try
        {
            string customLabel = editor.Text.Trim();
            if (_brain is not null)
            {
                JsonObject currentMetadata = ParseObjectString(_brain["metadataJson"], "behavior brain metadata");
                string currentLabel = LegacySaveDocument.ReadString(
                    (currentMetadata["miscPanelUseMetadata"] as JsonObject)?["customLabel"]);
                if (currentLabel == customLabel)
                    return;
            }
            JsonObject brain = EnsureWritableBrain();
            JsonObject metadata = ParseObjectString(brain["metadataJson"], "behavior brain metadata");
            JsonObject miscMetadata = metadata["miscPanelUseMetadata"] as JsonObject ?? new JsonObject();
            miscMetadata["version"] = 1;
            miscMetadata["customLabel"] = customLabel;
            metadata["miscPanelUseMetadata"] = miscMetadata;
            brain["metadataJson"] = metadata.ToJsonString();
            editor.Text = customLabel;
            panelNode.Title = customLabel.Length > 0 ? customLabel : "Custom";
            _layoutDirty = true;
            _onChanged();
            RebuildCanvas();
            _status.Text = "Custom panel title updated in memory. Use Save in GBNG to persist the project.";
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            _status.Text = exception.Message;
        }
    }

    private void DuplicatePanel(string panelUseId)
    {
        var addedUseIds = new List<string>();
        JsonArray? targetUses = null;
        string originalBrainId = _actor.BrainName;
        JsonObject? originalBrain = _brain;
        bool previousLayoutDirty = _layoutDirty;
        try
        {
            JsonArray currentUses = _brain?["behaviorUses"] as JsonArray
                ?? throw new InvalidDataException("This behavior brain has no assigned uses.");
            JsonObject currentPanelUse = FindUse(currentUses, panelUseId)
                ?? throw new InvalidDataException("The selected panel no longer exists.");
            string uri = LegacySaveDocument.ReadString(currentPanelUse["behaviorUri"]);
            JsonObject? panelData = GetBehaviorMetadata(uri)?["cardSystemPanelData"] as JsonObject;
            if (!LegacySaveDocument.ReadBool(panelData?["isPanel"]))
                throw new InvalidDataException("The selected behavior is not a panel.");
            JsonArray definitions = _getPropertyDefinitions(uri)
                ?? throw new InvalidDataException("This panel's card slot definitions are unavailable.");

            JsonObject brain = EnsureWritableBrain();
            targetUses = brain["behaviorUses"] as JsonArray
                ?? throw new InvalidDataException("This behavior brain has no assigned uses.");
            JsonObject sourceUse = FindUse(targetUses, panelUseId)
                ?? throw new InvalidDataException("The selected panel no longer exists.");
            JsonObject panelClone = sourceUse.DeepClone().AsObject();
            string panelCloneId = NewUseId(targetUses);
            panelClone["id"] = panelCloneId;
            targetUses.Add(panelClone);
            addedUseIds.Add(panelCloneId);

            JsonObject panelUseMetadata = ParseObjectString(panelClone["metadataJson"], "duplicated panel-use metadata");
            GraphNode? sourceNode = _panelNodes
                .FirstOrDefault(entry => LegacySaveDocument.ReadString(entry.Use["id"]) == panelUseId).Node;
            Vector2 sourcePosition = sourceNode?.PositionOffset
                ?? ReadVector2(panelUseMetadata["position"], new Vector2(50 + _panelNodes.Count * 380, 60));
            panelUseMetadata["version"] = 1;
            panelUseMetadata["position"] = CreateVector2(sourcePosition + new Vector2(80, -80));
            panelClone["metadataJson"] = panelUseMetadata.ToJsonString();

            foreach (JsonNode? node in definitions)
            {
                if (node is not JsonObject definition
                    || LegacySaveDocument.ReadString(definition["type"]) != "CardDeck")
                    continue;
                string propertyName = LegacySaveDocument.ReadString(definition["variableName"]);
                if (propertyName.Length == 0
                    || ReadAssignedValue(sourceUse, propertyName) is not JsonArray sourceCardIds)
                    continue;

                var clonedCardIds = new JsonArray();
                foreach (JsonNode? cardIdNode in sourceCardIds)
                {
                    string sourceCardId = LegacySaveDocument.ReadString(cardIdNode);
                    if (sourceCardId.Length == 0)
                        continue;
                    JsonObject sourceCardUse = FindUse(targetUses, sourceCardId)
                        ?? throw new InvalidDataException($"Panel deck '{propertyName}' references missing card use '{sourceCardId}'.");
                    JsonObject cardClone = sourceCardUse.DeepClone().AsObject();
                    string cardCloneId = NewUseId(targetUses);
                    cardClone["id"] = cardCloneId;
                    targetUses.Add(cardClone);
                    addedUseIds.Add(cardCloneId);
                    clonedCardIds.Add(cardCloneId);
                }
                WriteAssignedValue(panelClone, propertyName, clonedCardIds);
            }

            _layoutDirty = true;
            _onChanged();
            RebuildCanvas();
            _status.Text = "Panel duplicated with its card uses and properties. Use Save in GBNG to persist the project.";
        }
        catch (Exception exception)
        {
            if (targetUses is not null)
            {
                for (int index = addedUseIds.Count - 1; index >= 0; index--)
                    RemoveBehaviorUse(targetUses, addedUseIds[index]);
            }
            bool brainWasCloned = RestoreBrainAfterFailedMutation(originalBrainId, originalBrain);
            if (addedUseIds.Count > 0 || brainWasCloned)
                _onChanged();
            _layoutDirty = previousLayoutDirty;
            _status.Text = $"Could not duplicate panel: {exception.Message}";
        }
    }

    private void DeletePanel(string panelUseId)
    {
        try
        {
            JsonObject brain = EnsureWritableBrain();
            if (brain["behaviorUses"] is not JsonArray uses)
                throw new InvalidDataException("This behavior brain has no assigned uses.");
            JsonObject panelUse = FindUse(uses, panelUseId)
                ?? throw new InvalidDataException("The selected panel no longer exists.");
            string uri = LegacySaveDocument.ReadString(panelUse["behaviorUri"]);
            JsonObject? panelData = GetBehaviorMetadata(uri)?["cardSystemPanelData"] as JsonObject;
            if (panelData is null || !LegacySaveDocument.ReadBool(panelData["isPanel"]))
                throw new InvalidDataException("The selected behavior is not a panel.");
            JsonArray? definitions = _getPropertyDefinitions(uri);
            if (definitions is null)
                throw new InvalidDataException("This panel's card slot definitions are unavailable.");

            var cardUseIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonNode? node in definitions)
            {
                if (node is not JsonObject definition
                    || LegacySaveDocument.ReadString(definition["type"]) != "CardDeck")
                    continue;
                string propertyName = LegacySaveDocument.ReadString(definition["variableName"]);
                if (propertyName.Length == 0 || ReadAssignedValue(panelUse, propertyName) is not JsonArray assigned)
                    continue;
                foreach (JsonNode? cardId in assigned)
                {
                    string id = LegacySaveDocument.ReadString(cardId);
                    if (id.Length > 0 && id != panelUseId)
                        cardUseIds.Add(id);
                }
            }
            foreach (string cardUseId in cardUseIds)
            {
                RemoveUseFromDecks(uses, cardUseId);
                RemoveBehaviorUse(uses, cardUseId);
            }
            RemoveBehaviorUse(uses, panelUseId);
            ApplyCardDeckChange();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or OverflowException)
        {
            _status.Text = exception.Message;
        }
    }

    private void AddSelectedPanel()
    {
        if (_panelPicker.Selected < 0)
            return;

        string uri = _panelPicker.GetItemMetadata(_panelPicker.Selected).AsString();
        var addedUseIds = new List<string>();
        JsonArray? targetUses = null;
        string originalBrainId = _actor.BrainName;
        JsonObject? originalBrain = _brain;
        bool previousLayoutDirty = _layoutDirty;
        try
        {
            JsonObject? panelData = GetBehaviorMetadata(uri)?["cardSystemPanelData"] as JsonObject;
            if (!LegacySaveDocument.ReadBool(panelData?["isPanel"]) || IsHiddenPanel(panelData))
                throw new InvalidDataException("The selected behavior is not an available built-in panel.");

            JsonObject brain = EnsureWritableBrain();
            targetUses = brain["behaviorUses"] as JsonArray
                ?? throw new InvalidDataException("This behavior brain has no assigned uses.");
            JsonObject panelUse = CreateBehaviorUse(NewUseId(targetUses), uri);
            targetUses.Add(panelUse);
            addedUseIds.Add(LegacySaveDocument.ReadString(panelUse["id"]));
            _layoutDirty = true;

            _onChanged();
            JsonArray definitions = _getPropertyDefinitions(uri)
                ?? throw new InvalidDataException($"Property definitions for '{uri}' are unavailable.");
            foreach (JsonNode? node in definitions)
            {
                if (node is not JsonObject definition
                    || LegacySaveDocument.ReadString(definition["type"]) != "CardDeck")
                    continue;
                string propertyName = LegacySaveDocument.ReadString(definition["variableName"]);
                if (propertyName.Length == 0
                    || (definition["deckOptions"] as JsonObject)?["defaultCardURIs"] is not JsonArray defaultUris)
                    continue;

                var defaultUseIds = new JsonArray();
                foreach (JsonNode? defaultUriNode in defaultUris)
                {
                    string defaultUri = LegacySaveDocument.ReadString(defaultUriNode);
                    if (defaultUri.Length == 0)
                        continue;
                    string cardUseId = NewUseId(targetUses);
                    targetUses.Add(CreateBehaviorUse(cardUseId, defaultUri));
                    addedUseIds.Add(cardUseId);
                    defaultUseIds.Add(cardUseId);
                }
                if (defaultUseIds.Count > 0)
                    WriteAssignedValue(panelUse, propertyName, defaultUseIds);
            }

            _onChanged();
            RebuildCanvas();
            _status.Text = "Panel added with its default cards. Use Save in GBNG to persist the project.";
        }
        catch (Exception exception)
        {
            if (targetUses is not null)
            {
                for (int index = addedUseIds.Count - 1; index >= 0; index--)
                    RemoveBehaviorUse(targetUses, addedUseIds[index]);
            }
            bool brainWasCloned = RestoreBrainAfterFailedMutation(originalBrainId, originalBrain);
            if (addedUseIds.Count > 0 || brainWasCloned)
                _onChanged();
            _layoutDirty = previousLayoutDirty;
            _status.Text = $"Could not add panel: {exception.Message}";
        }
    }

    private void CreateCustomCard()
    {
        try
        {
            _createCustomCard();
            _layoutDirty = true;
            RebuildCanvas();
            _status.Text = "Custom card created in memory. Use Save in GBNG to persist the project.";
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or OverflowException)
        {
            _status.Text = exception.Message;
        }
    }

    private void ReturnCardToCustom(string cardUseId)
    {
        try
        {
            JsonObject brain = EnsureWritableBrain();
            if (brain["behaviorUses"] is not JsonArray uses)
                throw new InvalidDataException("This behavior brain has no assigned uses.");
            JsonObject cardUse = FindUse(uses, cardUseId)
                ?? throw new InvalidDataException("The selected card use no longer exists.");
            if (!IsCustomPanelCard(LegacySaveDocument.ReadString(cardUse["behaviorUri"])))
                throw new InvalidDataException("Only Custom-category cards can be returned to the Custom panel.");
            RemoveUseFromDecks(uses, cardUseId);
            ApplyCardDeckChange();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or OverflowException)
        {
            _status.Text = exception.Message;
        }
    }

    private void RemoveCustomCard(string cardUseId)
    {
        try
        {
            JsonObject brain = EnsureWritableBrain();
            if (brain["behaviorUses"] is not JsonArray uses)
                throw new InvalidDataException("This behavior brain has no assigned uses.");
            JsonObject cardUse = FindUse(uses, cardUseId)
                ?? throw new InvalidDataException("The selected card use no longer exists.");
            if (!IsCustomPanelCard(LegacySaveDocument.ReadString(cardUse["behaviorUri"])))
                throw new InvalidDataException("The selected use is not a Custom-category card.");
            RemoveUseFromDecks(uses, cardUseId);
            RemoveBehaviorUse(uses, cardUseId);
            ApplyCardDeckChange();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or OverflowException)
        {
            _status.Text = exception.Message;
        }
    }

    private void RebuildCanvas()
    {
        _building = true;
        try
        {
            foreach (GraphNode node in _canvas.GetChildren().OfType<GraphNode>().ToArray())
            {
                _canvas.RemoveChild(node);
                node.QueueFree();
            }
            _panelNodes.Clear();
            _noteNodes.Clear();
            _resizeBoundNoteNodes.Clear();
            _customNode = null;
            _canvasBuildGeneration++;
        }
        finally
        {
            _building = false;
        }
        PopulateCanvas();
    }

    private JsonObject EnsureWritableBrain()
    {
        JsonObject? database = _document.BehaviorDatabase;
        if (database?["brainIds"] is not JsonArray brainIds || database["brains"] is not JsonArray brains
            || brainIds.Count != brains.Count)
            throw new InvalidDataException("The .voos brainIds and brains tables are invalid.");
        int brainIndex = FindStringIndex(brainIds, _actor.BrainName);
        if (brainIndex < 0 || brainIndex >= brains.Count || brains[brainIndex] is not JsonObject brain)
            throw new InvalidDataException("This actor's behavior brain no longer exists.");
        if (_actor.BrainName != DefaultBrainId)
        {
            _brain = brain;
            return brain;
        }

        JsonObject clone = JsonNode.Parse(brain.ToJsonString())!.AsObject();
        string cloneId;
        do
        {
            cloneId = Guid.NewGuid().ToString("N");
        } while (FindStringIndex(brainIds, cloneId) >= 0);
        brainIds.Add(cloneId);
        brains.Add(clone);
        _actor.Data["brainName"] = cloneId;
        _brain = clone;
        return clone;
    }

    private bool RestoreBrainAfterFailedMutation(string originalBrainId, JsonObject? originalBrain)
    {
        if (originalBrainId != DefaultBrainId || _actor.BrainName == originalBrainId)
            return false;
        if (_document.BehaviorDatabase is JsonObject database
            && database["brainIds"] is JsonArray brainIds
            && database["brains"] is JsonArray brains)
        {
            int cloneIndex = FindStringIndex(brainIds, _actor.BrainName);
            if (cloneIndex >= 0 && cloneIndex < brains.Count)
            {
                brainIds.RemoveAt(cloneIndex);
                brains.RemoveAt(cloneIndex);
            }
        }
        _actor.Data["brainName"] = originalBrainId;
        _brain = originalBrain;
        return true;
    }


    private void RemoveUseFromDecks(JsonArray uses, string cardUseId)
    {
        foreach (JsonNode? node in uses)
        {
            if (node is not JsonObject use)
                continue;
            string uri = LegacySaveDocument.ReadString(use["behaviorUri"]);
            JsonObject? panelData = (GetBehaviorMetadata(uri)?["cardSystemPanelData"] as JsonObject);
            if (panelData is null || !LegacySaveDocument.ReadBool(panelData["isPanel"]))
                continue;
            JsonArray? definitions = _getPropertyDefinitions(uri);
            if (definitions is null)
                continue;
            foreach (JsonNode? definitionNode in definitions)
            {
                if (definitionNode is not JsonObject definition
                    || LegacySaveDocument.ReadString(definition["type"]) != "CardDeck")
                    continue;
                string name = LegacySaveDocument.ReadString(definition["variableName"]);
                if (name.Length == 0 || ReadAssignedValue(use, name) is not JsonArray assigned)
                    continue;
                var remaining = new JsonArray();
                bool changed = false;
                foreach (JsonNode? idNode in assigned)
                {
                    if (LegacySaveDocument.ReadString(idNode) == cardUseId)
                        changed = true;
                    else
                        remaining.Add(idNode?.DeepClone());
                }
                if (changed)
                    WriteAssignedValue(use, name, remaining);
            }
        }
    }

    private static void RemoveBehaviorUse(JsonArray uses, string useId)
    {
        for (int index = uses.Count - 1; index >= 0; index--)
        {
            if (uses[index] is JsonObject use && LegacySaveDocument.ReadString(use["id"]) == useId)
                uses.RemoveAt(index);
        }
    }

    private static JsonObject? FindUse(JsonArray uses, string useId)
    {
        foreach (JsonNode? node in uses)
        {
            if (node is JsonObject use && LegacySaveDocument.ReadString(use["id"]) == useId)
                return use;
        }
        return null;
    }

    private static string NewUseId(JsonArray uses)
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        } while (FindUse(uses, id) is not null);
        return id;
    }

    private static JsonObject CreateBehaviorUse(string useId, string behaviorUri)
        => new()
        {
            ["id"] = useId,
            ["behaviorUri"] = behaviorUri,
            ["metadataJson"] = "",
            ["propertyAssignments"] = new JsonArray()
        };


    private bool IsCardInCategory(string uri, string category)
    {
        JsonObject? metadata = GetBehaviorMetadata(uri);
        if (metadata is null)
            return uri.StartsWith("embedded:", StringComparison.Ordinal) && category == CustomCategory;
        if (metadata["cardSystemCardData"] is not JsonObject cardData
            || !LegacySaveDocument.ReadBool(cardData["isCard"])
            || cardData["categories"] is not JsonArray categories)
            return false;
        return categories.Any(node => LegacySaveDocument.ReadString(node) == category);
    }

    private bool IsCustomPanelCard(string uri)
    {
        JsonObject? metadata = GetBehaviorMetadata(uri);
        if (LegacySaveDocument.ReadBool((metadata?["cardSystemPanelData"] as JsonObject)?["isPanel"]))
            return false;
        JsonObject? cardData = metadata?["cardSystemCardData"] as JsonObject;
        if (cardData is not null && !LegacySaveDocument.ReadBool(cardData["isCard"], true))
            return false;
        return HasCustomCategory(cardData);
    }

    private string GetCardDisplayName(string uri)
    {
        string title = GetCardTitle(uri);
        if (title.Length > 0 && title != uri)
            return title;
        if (uri.StartsWith("builtin:", StringComparison.Ordinal))
            return uri["builtin:".Length..];
        if (uri.StartsWith("embedded:", StringComparison.Ordinal))
        {
            string id = uri["embedded:".Length..];
            JsonObject? database = _document.BehaviorDatabase;
            if (database?["behaviorIds"] is JsonArray ids && database["behaviors"] is JsonArray behaviors)
            {
                int index = FindStringIndex(ids, id);
                if (index >= 0 && index < behaviors.Count && behaviors[index] is JsonObject behavior)
                {
                    string label = LegacySaveDocument.ReadString(behavior["label"]);
                    if (label.Length > 0 && label != "Custom")
                        return label;
                }
            }
            return $"Custom · {(id.Length > 8 ? id[..8] : id)}";
        }
        return uri;
    }

    private GraphNode CreateCustomNode(JsonArray uses, HashSet<string> assignedCardIds, int panelIndex)
    {
        JsonObject brainMetadata = _brain is null
            ? new JsonObject()
            : ParseObjectString(_brain["metadataJson"], "behavior brain metadata");
        JsonObject miscMetadata = brainMetadata["miscPanelUseMetadata"] as JsonObject ?? new JsonObject();
        string title = LegacySaveDocument.ReadString(miscMetadata["customLabel"], "Custom");
        var graphNode = new GraphNode
        {
            Name = $"custom_{panelIndex}_{_canvasBuildGeneration}",
            Title = title,
            PositionOffset = ReadVector2(miscMetadata["position"], new Vector2(50 + panelIndex * 380, 60)),
            CustomMinimumSize = new Vector2(310, 0),
            Resizable = true,
            Draggable = true,
            Selectable = true
        };
        graphNode.AddThemeColorOverride("title_color", new Color("#aeb8c2"));
        graphNode.AddThemeFontSizeOverride("title_font_size", 24);
        graphNode.AddChild(CreateCanvasLabel("Custom-category cards not assigned to another panel."));

        var customTitle = new LineEdit
        {
            Name = "CustomPanelTitle",
            Text = LegacySaveDocument.ReadString(miscMetadata["customLabel"]),
            PlaceholderText = "Custom",
            TooltipText = "Custom panel title. Leave blank to restore Custom.",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var titleRow = new HBoxContainer();
        titleRow.AddChild(customTitle);
        var applyTitle = new Button { Name = "ApplyCustomPanelTitle", Text = "Apply title" };
        applyTitle.Pressed += () => SaveCustomPanelTitle(graphNode, customTitle);
        titleRow.AddChild(applyTitle);
        graphNode.AddChild(titleRow);
        var createCustomCard = new Button { Name = "CreateCustomCard", Text = "Create custom card" };
        createCustomCard.Pressed += CreateCustomCard;
        graphNode.AddChild(createCustomCard);
        int count = 0;
        foreach (JsonNode? node in uses)
        {
            if (node is not JsonObject use)
                continue;
            string id = LegacySaveDocument.ReadString(use["id"]);
            string uri = LegacySaveDocument.ReadString(use["behaviorUri"]);
            if (id.Length == 0 || id == "__DEFAULT_BRAIN__" || uri == "builtin:Default Behavior" || assignedCardIds.Contains(id))
                continue;
            if (!IsCustomPanelCard(uri))
                continue;
            JsonObject? cardData = GetBehaviorMetadata(uri)?["cardSystemCardData"] as JsonObject;
            var row = new HBoxContainer();
            row.AddChild(new Label
            {
                Text = $"  {LegacySaveDocument.ReadString(cardData?["title"], uri)}",
                TooltipText = id,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.Word
            });
            var removeCard = new Button { Name = $"RemoveCustomCard_{id}", Text = "Remove" };
            removeCard.Pressed += () => RemoveCustomCard(id);
            row.AddChild(removeCard);
            graphNode.AddChild(row);
            count++;
        }
        if (count == 0)
            graphNode.AddChild(CreateCanvasLabel("(empty)"));
        return graphNode;
    }

    private GraphNode CreateNoteNode(JsonObject data, int index)
    {
        Vector2 size = ReadVector2(data["size"], new Vector2(300, 200));
        size = new Vector2(Math.Max(size.X, 180f), Math.Max(size.Y, 120f));
        var graphNode = new GraphNode
        {
            Name = $"note_{index}_{_canvasBuildGeneration}",
            Title = "Note",
            PositionOffset = ReadVector2(data["position"], new Vector2(60 + index * 340, 160)),
            CustomMinimumSize = size,
            Resizable = true,
            Draggable = true,
            Selectable = true
        };
        graphNode.AddThemeColorOverride("title_color", new Color("#f2cf78"));
        graphNode.AddThemeFontSizeOverride("title_font_size", 22);
        var text = new TextEdit
        {
            Text = LegacySaveDocument.ReadString(data["content"]),
            CustomMinimumSize = new Vector2(Math.Max(size.X - 24f, 130f), Math.Max(size.Y - 48f, 80f)),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            WrapMode = TextEdit.LineWrappingMode.Boundary
        };
        text.AddThemeFontSizeOverride("font_size", 20);
        text.TextChanged += () =>
        {
            if (_building)
                return;
            data["content"] = text.Text;
            PersistNotes();
        };
        graphNode.AddChild(text);
        var delete = new Button { Text = "Delete note" };
        delete.Pressed += () => DeletePanelNote(graphNode, data);
        graphNode.AddChild(delete);
        _noteNodes.Add((graphNode, text, data));
        return graphNode;
    }

    private void BindNoteResize(GraphNode node, JsonObject data, TextEdit text)
    {
        if (!_resizeBoundNoteNodes.Add(node))
            return;
        node.Resized += () =>
        {
            if (_building || !_viewInitialized || _brain is null)
                return;
            Vector2 size = node.Size;
            if (ReadVector2(data["size"], size).IsEqualApprox(size))
                return;
            data["size"] = CreateVector2(size);
            data["content"] = text.Text;
            PersistNotes();
        };
    }

    private void AddPanelNote()
    {
        if (_brain is null)
            return;
        Vector2 viewCenter = new(_canvas.Size.X / 2f, (_canvas.Size.Y + 48f) / 2f);
        Vector2 size = new(300f, 200f);
        Vector2 position = (viewCenter + _canvas.ScrollOffset) / Math.Max(_canvas.Zoom, 0.001f) - size / 2f;
        var data = new JsonObject
        {
            ["position"] = CreateVector2(position),
            ["size"] = CreateVector2(size),
            ["content"] = ""
        };
        _notes.Add(data);
        GraphNode node = CreateNoteNode(data, _nextNoteNodeId++);
        _canvas.AddChild(node);
        BindNoteResize(node, data, _noteNodes[^1].Text);
        PersistNotes();
    }

    private void DeletePanelNote(GraphNode node, JsonObject data)
    {
        for (int index = 0; index < _notes.Count; index++)
        {
            if (ReferenceEquals(_notes[index], data))
            {
                _notes.RemoveAt(index);
                break;
            }
        }
        _noteNodes.RemoveAll(note => note.Node == node);
        _resizeBoundNoteNodes.Remove(node);
        _canvas.RemoveChild(node);
        node.QueueFree();
        PersistNotes();
    }

    private void PersistNotes()
    {
        if (_brain is null)
            return;
        JsonObject brainMetadata = ParseObjectString(_brain["metadataJson"], "behavior brain metadata");
        brainMetadata["panelNotes"] = _notes.DeepClone();
        _brain["metadataJson"] = brainMetadata.ToJsonString();
        _layoutDirty = true;
        _status.Text = "Panel notes updated in memory. Use Save in GBNG to persist the project.";
    }

    private static Label CreateCanvasLabel(string text, string tooltip = "")
    {
        var label = new Label
        {
            Text = text,
            TooltipText = tooltip,
            AutowrapMode = TextServer.AutowrapMode.Word
        };
        label.AddThemeFontSizeOverride("font_size", 22);
        return label;
    }

    private void SaveNodePositions()
    {
        if (_building || _brain is null)
            return;
        foreach ((GraphNode node, JsonObject use) in _panelNodes)
        {
            JsonObject metadata = ParseObjectString(use["metadataJson"], "panel-use metadata");
            metadata["version"] = 1;
            metadata["position"] = CreateVector2(node.PositionOffset);
            use["metadataJson"] = metadata.ToJsonString();
        }

        GraphNode? customNode = _customNode;
        if (customNode is not null)
        {
            JsonObject brainMetadata = ParseObjectString(_brain["metadataJson"], "behavior brain metadata");
            JsonObject miscMetadata = brainMetadata["miscPanelUseMetadata"] as JsonObject ?? new JsonObject();
            miscMetadata["version"] = 1;
            miscMetadata["position"] = CreateVector2(customNode.PositionOffset);
            brainMetadata["miscPanelUseMetadata"] = miscMetadata;
            _brain["metadataJson"] = brainMetadata.ToJsonString();
        }
        foreach ((GraphNode node, TextEdit text, JsonObject data) in _noteNodes)
        {
            data["position"] = CreateVector2(node.PositionOffset);
            data["size"] = CreateVector2(node.Size);
            data["content"] = text.Text;
        }
        PersistNotes();
        SaveViewStateToBrain();
        _layoutDirty = true;
        _status.Text = "Panel layout updated in memory. Use Save in GBNG to persist the project.";
    }

    private void SaveViewState()
    {
        if (_brain is null)
            return;
        SaveViewStateToBrain();
        _layoutDirty = true;
        _status.Text = "Canvas view updated in memory. Use Save in GBNG to persist the project.";
    }

    private void SaveViewStateToBrain()
    {
        if (_brain is null)
            return;
        JsonObject brainMetadata = ParseObjectString(_brain["metadataJson"], "behavior brain metadata");
        JsonObject viewState = brainMetadata["panelViewState"] as JsonObject ?? new JsonObject();
        viewState["version"] = 1;
        viewState["scale"] = _canvas.Zoom;
        viewState["viewPosition"] = CreateVector2(_canvas.ScrollOffset);
        viewState["customLayout"] = true;
        viewState["godotGraphEdit"] = true;
        brainMetadata["panelViewState"] = viewState;
        _brain["metadataJson"] = brainMetadata.ToJsonString();
    }

    private void RestoreViewState(JsonObject brain)
    {
        JsonObject metadata = ParseObjectString(brain["metadataJson"], "behavior brain metadata");
        if (metadata["panelViewState"] is JsonObject viewState
            && LegacySaveDocument.ReadBool(viewState["godotGraphEdit"]))
            _pendingViewState = viewState.DeepClone().AsObject();
    }

    private void CloseCanvas()
    {
        if (_layoutDirty)
            _onChanged();
        QueueFree();
    }

    private void LoadBuiltinMetadata()
    {
        var metadataPaths = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string metadataPath in LegacyBehaviorRuntime.EnumerateFiles(BuiltinBehaviorRoot, ".metaJson"))
        {
            string behaviorFileName = Path.GetFileNameWithoutExtension(metadataPath);
            if (!metadataPaths.TryAdd(behaviorFileName, metadataPath))
                metadataPaths[behaviorFileName] = null;
        }

        foreach (string path in LegacyBehaviorRuntime.EnumerateFiles(BuiltinBehaviorRoot, ".js.txt"))
        {
            if (path.Contains("/Specs/", StringComparison.Ordinal))
                continue;
            string name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
            string uri = $"builtin:{name}";
            string fileName = Path.GetFileName(path);
            JsonObject? metadata = metadataPaths.TryGetValue(fileName, out string? metadataPath) && metadataPath is not null
                ? ParseJsonObject(Godot.FileAccess.GetFileAsString(metadataPath), metadataPath)
                : null;
            if (!_builtinMetadata.TryAdd(uri, metadata))
                _builtinMetadata[uri] = null;
        }
    }

    private void PopulatePanelChoices()
    {
        _panelPicker.Clear();
        foreach (KeyValuePair<string, JsonObject?> entry in _builtinMetadata
            .OrderBy(item => LegacySaveDocument.ReadString((item.Value?["cardSystemPanelData"] as JsonObject)?["title"], item.Key), StringComparer.OrdinalIgnoreCase))
        {
            JsonObject? panelData = entry.Value?["cardSystemPanelData"] as JsonObject;
            if (!LegacySaveDocument.ReadBool(panelData?["isPanel"]) || IsHiddenPanel(panelData))
                continue;
            string title = LegacySaveDocument.ReadString(panelData?["title"], entry.Key);
            _panelPicker.AddItem(title);
            _panelPicker.SetItemMetadata(_panelPicker.ItemCount - 1, entry.Key);
        }
        _panelPicker.Disabled = _panelPicker.ItemCount == 0;
        if (_panelPicker.ItemCount > 0)
            _panelPicker.Select(0);
    }

    private static bool IsHiddenPanel(JsonObject? panelData)
    {
        JsonNode? hiddenNode = panelData?["hidden"];
        if (hiddenNode is null)
            return false;
        if (hiddenNode is JsonValue value && value.TryGetValue(out bool hidden))
            return hidden;
        if (bool.TryParse(LegacySaveDocument.ReadString(hiddenNode), out bool legacyHidden))
            return legacyHidden;
        throw new InvalidDataException("Panel hidden metadata must be a boolean.");
    }

    private JsonObject? GetBehaviorMetadata(string uri)
    {
        if (uri.StartsWith("builtin:", StringComparison.Ordinal))
        {
            _builtinMetadata.TryGetValue(uri, out JsonObject? metadata);
            return metadata;
        }
        if (!uri.StartsWith("embedded:", StringComparison.Ordinal))
            return null;
        string id = uri["embedded:".Length..];
        JsonObject? database = _document.BehaviorDatabase;
        if (database?["behaviorIds"] is not JsonArray ids || database["behaviors"] is not JsonArray behaviors)
            return null;
        int index = FindStringIndex(ids, id);
        if (index < 0 || index >= behaviors.Count || behaviors[index] is not JsonObject behavior)
            return null;
        string encoded = LegacySaveDocument.ReadString(behavior["metadataJson"]);
        return encoded.Length == 0 ? null : ParseJsonObject(encoded, $"metadata for '{uri}'");
    }

    private string GetCardTitle(string uri)
    {
        JsonObject? metadata = GetBehaviorMetadata(uri);
        return LegacySaveDocument.ReadString((metadata?["cardSystemCardData"] as JsonObject)?["title"], uri);
    }

    private static bool HasCustomCategory(JsonObject? cardData)
    {
        if (cardData?["categories"] is not JsonArray categories || categories.Count == 0)
            return true;
        foreach (JsonNode? category in categories)
        {
            if (LegacySaveDocument.ReadString(category) == CustomCategory)
                return true;
        }
        return false;
    }

    private static JsonNode? ReadAssignedValue(JsonObject use, string propertyName)
    {
        if (use["propertyAssignments"] is not JsonArray assignments)
            return null;
        foreach (JsonNode? node in assignments)
        {
            if (node is not JsonObject assignment
                || LegacySaveDocument.ReadString(assignment["propertyName"]) != propertyName)
                continue;
            string encoded = LegacySaveDocument.ReadString(assignment["valueJson"]);
            if (encoded.Length == 0)
                return null;
            JsonNode? parsed = JsonNode.Parse(encoded);
            return parsed is JsonObject envelope && envelope.ContainsKey("value")
                ? envelope["value"]?.DeepClone()
                : parsed?.DeepClone();
        }
        return null;
    }

    private static void WriteAssignedValue(JsonObject use, string propertyName, JsonNode value)
    {
        JsonArray assignments;
        if (use["propertyAssignments"] is JsonArray existing)
        {
            assignments = existing;
        }
        else if (use["propertyAssignments"] is null)
        {
            assignments = new JsonArray();
            use["propertyAssignments"] = assignments;
        }
        else
        {
            throw new InvalidDataException("The behavior use propertyAssignments field must be an array.");
        }

        JsonObject? assignment = assignments
            .OfType<JsonObject>()
            .FirstOrDefault(candidate => LegacySaveDocument.ReadString(candidate["propertyName"]) == propertyName);
        var envelope = new JsonObject { ["value"] = value.DeepClone() };
        if (assignment is null)
        {
            assignments.Add(new JsonObject
            {
                ["propertyName"] = propertyName,
                ["valueJson"] = envelope.ToJsonString()
            });
            return;
        }
        assignment["valueJson"] = envelope.ToJsonString();
    }

    private static JsonObject ParseObjectString(JsonNode? node, string description)
    {
        string encoded = LegacySaveDocument.ReadString(node);
        if (encoded.Length == 0)
            return new JsonObject();
        return ParseJsonObject(encoded, description);
    }

    private static JsonObject ParseJsonObject(string encoded, string description)
    {
        try
        {
            return JsonNode.Parse(encoded) as JsonObject
                ?? throw new InvalidDataException($"The {description} must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {description} is not valid JSON.", exception);
        }
    }

    private static JsonObject CreateVector2(Vector2 value)
        => new() { ["x"] = value.X, ["y"] = value.Y };

    private static Vector2 ReadVector2(JsonNode? node, Vector2 fallback)
        => node is JsonObject vector
            ? new Vector2(
                LegacySaveDocument.ReadFloat(vector["x"], fallback.X),
                LegacySaveDocument.ReadFloat(vector["y"], fallback.Y))
            : fallback;

    private static Color ReadColor(JsonNode? node, Color fallback)
    {
        if (node is not JsonObject color)
            return fallback;
        return new Color(
            LegacySaveDocument.ReadFloat(color["r"], fallback.R),
            LegacySaveDocument.ReadFloat(color["g"], fallback.G),
            LegacySaveDocument.ReadFloat(color["b"], fallback.B),
            LegacySaveDocument.ReadFloat(color["a"], fallback.A));
    }

    private static int FindStringIndex(JsonArray values, string value)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (LegacySaveDocument.ReadString(values[index]) == value)
                return index;
        }
        return -1;
    }
}
