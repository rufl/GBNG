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
    private readonly LegacySaveDocument _document;
    private readonly LegacyActorData _actor;
    private readonly Action _onChanged;
    private readonly Func<string, JsonArray?> _getPropertyDefinitions;
    private readonly GraphEdit _canvas = new();
    private readonly Label _status = new();
    private readonly Dictionary<string, JsonObject?> _builtinMetadata = new(StringComparer.Ordinal);
    private readonly List<(GraphNode Node, JsonObject Use)> _panelNodes = new();
    private JsonObject? _brain;
    private bool _building;
    private bool _layoutDirty;
    private JsonObject? _pendingViewState;

    public LegacyPanelCanvasWindow(
        LegacySaveDocument document,
        LegacyActorData actor,
        Action onChanged,
        Func<string, JsonArray?> getPropertyDefinitions)
    {
        _document = document;
        _actor = actor;
        _onChanged = onChanged;
        _getPropertyDefinitions = getPropertyDefinitions;
        Title = $"Behavior panels — {actor.DisplayName}";
        Size = new Vector2I(1100, 760);
        CloseRequested += CloseCanvas;
        LoadBuiltinMetadata();
        BuildInterface();
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
            return;
        }
        FitCanvasToContent();
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
            Text = "Drag panels to preserve their layout. Edit card uses and properties in the behavior authoring window.",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.Word
        });
        var saveView = new Button { Text = "Save view" };
        saveView.Pressed += SaveViewState;
        toolbar.AddChild(saveView);
        var center = new Button { Text = "Reset view" };
        center.Pressed += ResetCanvasView;
        toolbar.AddChild(center);
        layout.AddChild(toolbar);

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
            _status.Text = $"{panelIndex} panel(s) plus the Custom panel. Layout changes remain in memory until GBNG saves the project.";
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
            Name = $"panel_{panelIndex}",
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
            JsonNode? value = ReadAssignedValue(use, propertyName);
            if (value is not JsonArray cardIds || cardIds.Count == 0)
            {
                graphNode.AddChild(CreateCanvasLabel("(empty)"));
                continue;
            }
            foreach (JsonNode? cardIdNode in cardIds)
            {
                string cardId = LegacySaveDocument.ReadString(cardIdNode);
                if (cardId.Length == 0)
                    continue;
                assignedCardIds.Add(cardId);
                string cardLabel = usesById.TryGetValue(cardId, out JsonObject? cardUse)
                    ? GetCardTitle(LegacySaveDocument.ReadString(cardUse["behaviorUri"]))
                    : $"Missing use · {cardId}";
                graphNode.AddChild(CreateCanvasLabel($"  {cardLabel}", cardId));
            }
        }
        if (!hasDeck)
            graphNode.AddChild(CreateCanvasLabel("No card decks."));
        return graphNode;
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
            Name = $"custom_{panelIndex}",
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
        int count = 0;
        foreach (JsonNode? node in uses)
        {
            if (node is not JsonObject use)
                continue;
            string id = LegacySaveDocument.ReadString(use["id"]);
            string uri = LegacySaveDocument.ReadString(use["behaviorUri"]);
            if (id.Length == 0 || id == "__DEFAULT_BRAIN__" || uri == "builtin:Default Behavior" || assignedCardIds.Contains(id))
                continue;
            JsonObject? metadata = GetBehaviorMetadata(uri);
            if (LegacySaveDocument.ReadBool((metadata?["cardSystemPanelData"] as JsonObject)?["isPanel"]))
                continue;
            JsonObject? cardData = metadata?["cardSystemCardData"] as JsonObject;
            if (cardData is not null && !LegacySaveDocument.ReadBool(cardData["isCard"], true))
                continue;
            if (!HasCustomCategory(cardData))
                continue;
            graphNode.AddChild(CreateCanvasLabel($"  {LegacySaveDocument.ReadString(cardData?["title"], uri)}", id));
            count++;
        }
        if (count == 0)
            graphNode.AddChild(CreateCanvasLabel("(empty)"));
        return graphNode;
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

        GraphNode? customNode = _canvas.GetNodeOrNull<GraphNode>($"custom_{_panelNodes.Count}");
        if (customNode is not null)
        {
            JsonObject brainMetadata = ParseObjectString(_brain["metadataJson"], "behavior brain metadata");
            JsonObject miscMetadata = brainMetadata["miscPanelUseMetadata"] as JsonObject ?? new JsonObject();
            miscMetadata["version"] = 1;
            miscMetadata["position"] = CreateVector2(customNode.PositionOffset);
            brainMetadata["miscPanelUseMetadata"] = miscMetadata;
            _brain["metadataJson"] = brainMetadata.ToJsonString();
        }
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
