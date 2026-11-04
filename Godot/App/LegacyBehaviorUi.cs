using System.Globalization;
using System.Xml;
using System.Text.Json.Nodes;
using GBNG.Assets;

using GBNG.Domain;
using Godot;

namespace GBNG.App;

public partial class LegacyBehaviorUi : Control
{
    private const float GameUiWidth = 1600f;
    private const float DefaultTextSize = 29.5f;
    private const float DefaultCharWidth = 13f;
    private const float DefaultLineHeight = 20f;
    private const string LegacyFontConfigPath = "res://Assets/third_party/Fonts/ProggyClean/ProggyClean.fnt";
    private const string LegacyFontAtlasPath = "res://Assets/third_party/Fonts/ProggyClean/ProggyClean.png";
    private readonly record struct BitmapGlyph(int X, int Y, int Width, int Height, int XOffset, int YOffset, int XAdvance);
    private readonly CanvasTexture _fontAtlas;
    private readonly Dictionary<int, BitmapGlyph> _fontGlyphs;
    private readonly int _fontBaseSize;
    private readonly LegacyRenderableCatalog? _catalog;
    private readonly HashSet<string> _unresolvedImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture2D> _importedImages = new(StringComparer.Ordinal);
    private readonly Dictionary<(Texture2D Texture, bool NoFilter), CanvasTexture> _filteredImages = new();
    private JsonArray _commands = new();
    private readonly Vector2[] _triangleOutline = new Vector2[4];
    private readonly Vector2[] _points = new Vector2[3];
    private readonly Color[] _textColorStack = new Color[128];
    private Action<string, string, string>? _sendMessage;
    private int _textColorStackCount;
    private float _drawScale = 1f;
    private static readonly char[] TextColorTerminators = { '"', '\'', '>' };
    private static readonly string[] TextTags = { "color", "b", "/color", "/b" };

    public LegacyBehaviorUi() : this(null) { }

    public LegacyBehaviorUi(LegacyRenderableCatalog? catalog)
    {
        _catalog = catalog;
        Texture2D? fontAtlas = ResourceLoader.Load<Texture2D>(LegacyFontAtlasPath);
        if (fontAtlas is null)
            throw new InvalidOperationException($"Could not load the bundled legacy UI font atlas at {LegacyFontAtlasPath}.");
        _fontAtlas = new CanvasTexture
        {
            DiffuseTexture = fontAtlas,
            TextureFilter = CanvasItem.TextureFilterEnum.Linear
        };

        var document = new XmlDocument();
        document.LoadXml(Godot.FileAccess.GetFileAsString(LegacyFontConfigPath));
        XmlElement? root = document.DocumentElement;
        if (root is null || root.Name != "font")
            throw new InvalidOperationException("The legacy UI bitmap font config must have a <font> root.");
        XmlElement? info = root.SelectSingleNode("info") as XmlElement;
        XmlElement? common = root.SelectSingleNode("common") as XmlElement;
        _fontBaseSize = ReadFontMetric(info, "size");
        if (_fontBaseSize <= 0
            || ReadFontMetric(common, "scaleW") != fontAtlas.GetWidth()
            || ReadFontMetric(common, "scaleH") != fontAtlas.GetHeight())
            throw new InvalidOperationException("The legacy UI bitmap font metrics do not match the bundled atlas.");

        XmlNodeList? pages = root.SelectNodes("pages/page");
        if (pages is null || pages.Count != 1 || pages[0] is not XmlElement page
            || ReadFontMetric(page, "id") != 0 || page.GetAttribute("file") != "ProggyClean.png")
            throw new InvalidOperationException("The legacy UI bitmap font must use the bundled ProggyClean.png atlas.");
        XmlNodeList? characters = root.SelectNodes("chars/char");
        if (characters is null)
            throw new InvalidOperationException("The legacy UI bitmap font has no character table.");
        _fontGlyphs = new Dictionary<int, BitmapGlyph>(characters.Count);
        foreach (XmlNode characterNode in characters)
        {
            if (characterNode is not XmlElement character)
                continue;
            _fontGlyphs.Add(ReadFontMetric(character, "id"), new BitmapGlyph(
                ReadFontMetric(character, "x"),
                ReadFontMetric(character, "y"),
                ReadFontMetric(character, "width"),
                ReadFontMetric(character, "height"),
                ReadFontMetric(character, "xoffset"),
                ReadFontMetric(character, "yoffset"),
                ReadFontMetric(character, "xadvance")));
        }
        if (_fontGlyphs.Count != ReadFontMetric(root.SelectSingleNode("chars") as XmlElement, "count"))
            throw new InvalidOperationException("The legacy UI bitmap font character count does not match its table.");

        MouseFilter = MouseFilterEnum.Pass;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public void ClearImageCache()
    {
        _importedImages.Clear();
        _filteredImages.Clear();
        _unresolvedImages.Clear();
    }
    public void SetMessageHandler(Action<string, string, string> sendMessage) => _sendMessage = sendMessage;

    public void SetCommands(JsonArray commands)
    {
        _commands = commands;
        QueueRedraw();
    }

    public override void _Draw()
    {
        Vector2 viewportSize = GetViewportRect().Size;
        float scale = viewportSize.X / GameUiWidth;
        if (scale <= 0f)
            return;

        _drawScale = scale;
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One * _drawScale);
        foreach (JsonNode? node in _commands)
        {
            if (node is JsonObject command)
                DrawCommand(command);
        }
        DrawSetTransform(Vector2.Zero);
    }

    internal bool IsInteractivePoint(Vector2 viewportPoint)
    {
        float scale = GetViewportRect().Size.X / GameUiWidth;
        return scale > 0f && FindButtonAtUiPoint(viewportPoint / scale) is not null;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouse)
            return;

        float scale = GetViewportRect().Size.X / GameUiWidth;
        if (scale <= 0f)
            return;
        JsonObject? command = FindButtonAtUiPoint(mouse.Position / scale);
        if (command is null)
            return;

        _sendMessage?.Invoke(
            LegacySaveDocument.ReadString(command["actorName"]),
            LegacySaveDocument.ReadString(command["clickMessageName"], "ButtonClicked"),
            LegacySaveDocument.ReadString(command["clickMessageArgJson"], "{}"));
        AcceptEvent();
    }

    private JsonObject? FindButtonAtUiPoint(Vector2 point)
    {
        for (int i = _commands.Count - 1; i >= 0; i--)
        {
            if (_commands[i] is not JsonObject command || LegacySaveDocument.ReadInt(command["cmd"], 0) != 3 || command["rect"] is not JsonObject rect)
                continue;

            float x = LegacySaveDocument.ReadFloat(rect["x"], 0f);
            float y = LegacySaveDocument.ReadFloat(rect["y"], 0f);
            float width = LegacySaveDocument.ReadFloat(rect["w"], 0f);
            float height = LegacySaveDocument.ReadFloat(rect["h"], 0f);
            if (point.X >= x && point.X < x + width && point.Y >= y && point.Y < y + height)
                return command;
        }
        return null;
    }

    private void DrawCommand(JsonObject command)
    {
        int commandId = LegacySaveDocument.ReadInt(command["cmd"], 0);
        float opacity = Mathf.Clamp(LegacySaveDocument.ReadFloat(command["opacity"], 1f), 0f, 1f);
        string style = LegacySaveDocument.ReadString(command["style"]);
        Color background = DecodeColor(LegacySaveDocument.ReadInt(command["backgroundColor"], 0xffffff), opacity);

        switch (commandId)
        {
            case 1:
                DrawText(command, opacity);
                break;
            case 2:
                if (TryReadRect(command["rect"], out Rect2 rect))
                    DrawStyledRect(rect, background, style);
                break;
            case 3:
                DrawButton(command, opacity, background);
                break;
            case 4:
                DrawImage(command, opacity);
                break;
            case 5:
                DrawCircle(command, background, style);
                break;
            case 6:
                DrawTriangle(command, background, style);
                break;
            case 7:
                DrawLine(command, background, style);
                break;
            case 8:
                DrawImageSlice(command, opacity);
                break;
        }
    }

    private void DrawText(JsonObject command, float opacity)
    {
        if (command["rect"] is not JsonObject rect)
            return;
        float x = LegacySaveDocument.ReadFloat(rect["x"], 0f);
        float y = LegacySaveDocument.ReadFloat(rect["y"], 0f);
        float size = GetTextSize(command);
        string text = LegacySaveDocument.ReadString(command["text"]);
        Color color = DecodeColor(LegacySaveDocument.ReadInt(command["textColor"], 0xffffff), opacity);
        float fontScale = size / _fontBaseSize;
        float lineHeight = DefaultLineHeight * size / DefaultTextSize;
        float lineTop = y;
        float lineStartX = x;
        int lineStart = 0;
        _textColorStackCount = 0;
        bool bold = false;

        for (int i = 0; i <= text.Length; i++)
        {
            bool isNewline = i < text.Length && text[i] == '\n';
            int tagEnd = -1;
            int tagNameStart = -1;
            int valueStart = -1;
            bool isTag = i < text.Length && TryReadTextTag(text, i, out tagEnd, out tagNameStart, out valueStart);
            if (i != text.Length && !isNewline && !isTag)
                continue;

            DrawTextRun(text, lineStart, i - lineStart, ref x, lineTop, fontScale, color, bold);
            if (isNewline)
            {
                x = lineStartX;
                lineTop += lineHeight;
                lineStart = i + 1;
                continue;
            }
            if (!isTag)
                break;

            string tag = text.Substring(tagNameStart, tagEnd - tagNameStart);
            if (tag.StartsWith("color", StringComparison.Ordinal) && valueStart >= 0)
            {
                if (_textColorStackCount < _textColorStack.Length)
                    _textColorStack[_textColorStackCount++] = color;
                color = ParseTextColor(text, valueStart);
            }
            else if (tag.StartsWith("#", StringComparison.Ordinal))
            {
                if (_textColorStackCount < _textColorStack.Length)
                    _textColorStack[_textColorStackCount++] = color;
                color = ParseTextColor(text, tagNameStart);
            }
            else if (tag == "b")
                bold = true;
            else if (tag == "/b")
                bold = false;
            else if (tag == "/color")
                color = _textColorStackCount > 0 ? _textColorStack[--_textColorStackCount] : DecodeColor(LegacySaveDocument.ReadInt(command["textColor"], 0xffffff), opacity);

            i = tagEnd;
            lineStart = i + 1;
        }
    }

    private void DrawTextRun(string text, int start, int length, ref float x, float lineTop, float fontScale, Color color, bool bold)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            if (!_fontGlyphs.TryGetValue(text[i], out BitmapGlyph glyph))
                continue;
            if (glyph.Width > 0 && glyph.Height > 0)
            {
                var source = new Rect2(glyph.X, glyph.Y, glyph.Width, glyph.Height);
                var destination = new Rect2(
                    x + glyph.XOffset * fontScale,
                    lineTop + glyph.YOffset * fontScale,
                    glyph.Width * fontScale,
                    glyph.Height * fontScale);
                DrawTextureRectRegion(_fontAtlas, destination, source, color);
                if (bold)
                {
                    destination.Position += Vector2.Right / _drawScale;
                    DrawTextureRectRegion(_fontAtlas, destination, source, color);
                }
            }
            x += glyph.XAdvance * fontScale;
        }
    }


    private static bool TryReadTextTag(string text, int start, out int tagEnd, out int tagNameStart, out int valueStart)
    {
        tagEnd = tagNameStart = valueStart = -1;
        if (text[start] != '<')
            return false;
        tagNameStart = start + 1;
        bool known = false;
        if (tagNameStart >= text.Length)
            return false;
        foreach (string candidate in TextTags)
        {
            if (text.AsSpan(tagNameStart).StartsWith(candidate, StringComparison.Ordinal)
                && (tagNameStart + candidate.Length == text.Length || text[tagNameStart + candidate.Length] < 'a' || text[tagNameStart + candidate.Length] > 'z'))
            {
                known = true;
                break;
            }
        }
        if (text[tagNameStart] == '#')
            known = true;
        if (!known)
            return false;
        tagEnd = text.IndexOf('>', tagNameStart);
        if (tagEnd < 0)
            return false;
        int equals = text.IndexOf('=', tagNameStart, tagEnd - tagNameStart);
        if (equals >= 0)
        {
            valueStart = equals + 1;
            while (valueStart < tagEnd && text[valueStart] == ' ')
                valueStart++;
            if (valueStart < tagEnd && (text[valueStart] == '"' || text[valueStart] == '\''))
                valueStart++;
        }
        return true;
    }

    private static Color ParseTextColor(string text, int start)
    {
        if (text[start] == '#')
        {
            start++;
            int end = text.IndexOfAny(TextColorTerminators, start);
            int length = (end >= 0 ? end : text.Length) - start;
            if (length == 3 && int.TryParse(text.AsSpan(start, length), System.Globalization.NumberStyles.HexNumber, null, out int rgb))
                return new Color(((rgb >> 8) & 0xf) / 15f, ((rgb >> 4) & 0xf) / 15f, (rgb & 0xf) / 15f);
            if (length >= 6 && int.TryParse(text.AsSpan(start, 6), System.Globalization.NumberStyles.HexNumber, null, out rgb))
                return new Color(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f);
            return Colors.White;
        }
        int endIndex = text.IndexOfAny(TextColorTerminators, start);
        string name = text.Substring(start, (endIndex >= 0 ? endIndex : text.Length) - start);
        return name switch
        {
            "black" => Colors.Black,
            "blue" => Colors.Blue,
            "green" => Colors.Green,
            "orange" => new Color(1f, 0.5f, 0f),
            "purple" => new Color(0.5f, 0f, 1f),
            "red" => Colors.Red,
            "white" => Colors.White,
            "yellow" => Colors.Yellow,
            _ => Colors.White
        };
    }

    private void DrawButton(JsonObject command, float opacity, Color background)
    {
        if (!TryReadRect(command["rect"], out Rect2 rect))
            return;
        DrawRect(rect, background);
        DrawRect(rect, DecodeColor(0xffffff, opacity), false, 1f);
        float size = GetTextSize(command);
        float fontScale = size / _fontBaseSize;
        Color textColor = DecodeColor(LegacySaveDocument.ReadInt(command["textColor"], 0xffffff), 1f);
        string text = LegacySaveDocument.ReadString(command["text"]);
        float textWidth = text.Length * DefaultCharWidth * size / DefaultTextSize;
        float lineHeight = DefaultLineHeight * size / DefaultTextSize;
        float x = rect.Position.X + (rect.Size.X - textWidth) * 0.5f;
        float lineTop = rect.Position.Y + (rect.Size.Y - lineHeight) * 0.5f;
        DrawTextRun(text, 0, text.Length, ref x, lineTop, fontScale, textColor, false);
    }

    private void DrawCircle(JsonObject command, Color color, string style)
    {
        if (command["points"] is not JsonArray points || points.Count == 0 || points[0] is not JsonObject center)
            return;
        var point = new Vector2(LegacySaveDocument.ReadFloat(center["x"], 0f), LegacySaveDocument.ReadFloat(center["y"], 0f));
        float radius = Math.Max(0f, LegacySaveDocument.ReadFloat(command["radius"], 0f));
        if (style == "BORDER")
            DrawArc(point, radius, 0f, Mathf.Tau, GetCircleSegmentCount(radius), color, 1f, true);
        else if (style == "DASHED")
            DrawDashedCircle(point, radius, color, GetCircleSegmentCount(radius));
        else
            DrawCircle(point, radius, color);
    }
    private int GetCircleSegmentCount(float radius)
    {
        float screenRadius = radius * GetViewportRect().Size.X / GameUiWidth;
        return Mathf.CeilToInt(Mathf.Clamp(Mathf.Pi * screenRadius, 3f, 300f));
    }


    private void DrawTriangle(JsonObject command, Color color, string style)
    {
        if (!ReadPoints(command["points"], 3))
            return;
        _triangleOutline[0] = _points[0];
        _triangleOutline[1] = _points[1];
        _triangleOutline[2] = _points[2];
        _triangleOutline[3] = _points[0];
        if (style == "BORDER")
            DrawPolyline(_triangleOutline, color, 1f, true);
        else if (style == "DASHED")
        {
            DrawDashedLine(_points[0], _points[1], color);
            DrawDashedLine(_points[1], _points[2], color);
            DrawDashedLine(_points[2], _points[0], color);
        }
        else
            DrawColoredPolygon(_points, color);
    }

    private void DrawLine(JsonObject command, Color color, string style)
    {
        if (!ReadPoints(command["points"], 2))
            return;
        if (style == "DASHED")
            DrawDashedLine(_points[0], _points[1], color);
        else
            DrawLine(_points[0], _points[1], color, 1f, true);
    }

    private void DrawStyledRect(Rect2 rect, Color color, string style)
    {
        if (style == "BORDER")
            DrawRect(rect, color, false, 1f);
        else if (style == "DASHED")
        {
            DrawDashedLine(rect.Position, new Vector2(rect.End.X, rect.Position.Y), color);
            DrawDashedLine(new Vector2(rect.End.X, rect.Position.Y), rect.End, color);
            DrawDashedLine(rect.End, new Vector2(rect.Position.X, rect.End.Y), color);
            DrawDashedLine(new Vector2(rect.Position.X, rect.End.Y), rect.Position, color);
        }
        else
            DrawRect(rect, color);
    }

    private void DrawDashedCircle(Vector2 center, float radius, Color color, int segments)
    {
        for (int i = 0; i < segments; i += 2)
        {
            float start = Mathf.Tau * i / segments;
            float end = Mathf.Tau * (i + 1) / segments;
            DrawArc(center, radius, start, end, 2, color, 1f, true);
        }
    }

    private void DrawDashedLine(Vector2 from, Vector2 to, Color color)
    {
        Vector2 delta = to - from;
        float length = delta.Length();
        if (length <= 0f)
            return;
        Vector2 direction = delta / length;
        const float dashLength = 6f;
        const float gapLength = 4f;
        for (float start = 0f; start < length; start += dashLength + gapLength)
            DrawLine(from + direction * start, from + direction * Math.Min(start + dashLength, length), color, 1f, true);
    }

    private void DrawImage(JsonObject command, float opacity)
    {
        if (!TryReadRect(command["rect"], out Rect2 rect))
            return;
        string imageId = LegacySaveDocument.ReadString(command["imageId"]);
        if (!TryLoadImage(imageId, out Texture2D? texture) || texture is null)
            return;
        if (rect.Size.X < 0f)
            rect.Size = new Vector2(texture.GetWidth(), rect.Size.Y);
        if (rect.Size.Y < 0f)
            rect.Size = new Vector2(rect.Size.X, texture.GetHeight() * rect.Size.X / Math.Max(1f, texture.GetWidth()));
        bool noFilter = LegacySaveDocument.ReadBool(command["noFilter"]);
        DrawTextureRect(GetFilteredImage(texture, noFilter), rect, false, new Color(1f, 1f, 1f, opacity));
    }

    private void DrawImageSlice(JsonObject command, float opacity)
    {
        if (!TryReadRect(command["rect"], out Rect2 destination) || !TryReadRect(command["srcRect"], out Rect2 source))
            return;
        if (!TryLoadImage(LegacySaveDocument.ReadString(command["imageId"]), out Texture2D? texture) || texture is null)
            return;
        bool noFilter = LegacySaveDocument.ReadBool(command["noFilter"]);
        DrawTextureRectRegion(GetFilteredImage(texture, noFilter), destination, source, new Color(1f, 1f, 1f, opacity));
    }
    private Texture2D GetFilteredImage(Texture2D texture, bool noFilter)
    {
        var key = (texture, noFilter);
        if (_filteredImages.TryGetValue(key, out CanvasTexture? filteredImage))
            return filteredImage;

        filteredImage = new CanvasTexture
        {
            DiffuseTexture = texture,
            TextureFilter = noFilter ? CanvasItem.TextureFilterEnum.Nearest : CanvasItem.TextureFilterEnum.Linear
        };
        _filteredImages.Add(key, filteredImage);
        return filteredImage;
    }


    private bool TryLoadImage(string imageId, out Texture2D? texture)
    {
        texture = null;
        if (imageId.StartsWith("custom:", StringComparison.Ordinal)
            && _catalog is not null
            && _catalog.TryGet(imageId, out RenderableEntry entry))
        {
            if (_importedImages.TryGetValue(imageId, out texture))
                return true;
            foreach (string imagePath in entry.ImagePaths)
            {
                try
                {
                    Image image = Image.LoadFromFile(imagePath);
                    if (image.IsEmpty())
                        continue;
                    texture = ImageTexture.CreateFromImage(image);
                    _importedImages.Add(imageId, texture);
                    break;
                }
                catch (Exception exception)
                {
                    GD.PushWarning($"Could not load behavior UI image '{imageId}': {exception.Message}");
                }
            }
        }
        else if (imageId.Length > 0
            && (imageId.StartsWith("res://", StringComparison.Ordinal) || imageId.StartsWith("user://", StringComparison.Ordinal)))
        {
            texture = ResourceLoader.Load<Texture2D>(imageId);
        }
        if (texture is not null)
            return true;
        if (_unresolvedImages.Add(imageId))
            GD.PushWarning($"Behavior UI image '{imageId}' is not a resolvable local asset.");
        return false;
    }

    private static bool TryReadRect(JsonNode? node, out Rect2 rect)
    {
        if (node is JsonObject value)
        {
            rect = new Rect2(
                LegacySaveDocument.ReadFloat(value["x"], 0f),
                LegacySaveDocument.ReadFloat(value["y"], 0f),
                LegacySaveDocument.ReadFloat(value["w"], 0f),
                LegacySaveDocument.ReadFloat(value["h"], 0f));
            return true;
        }
        rect = default;
        return false;
    }

    private bool ReadPoints(JsonNode? node, int expectedCount)
    {
        if (node is not JsonArray values || values.Count != expectedCount)
            return false;
        for (int i = 0; i < expectedCount; i++)
        {
            if (values[i] is not JsonObject point)
                return false;
            _points[i] = new Vector2(LegacySaveDocument.ReadFloat(point["x"], 0f), LegacySaveDocument.ReadFloat(point["y"], 0f));
        }
        return true;
    }


    private static int ReadFontMetric(XmlElement? element, string attribute)
    {
        if (element is null || !int.TryParse(element.GetAttribute(attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out int metric))
            throw new InvalidOperationException($"The legacy UI bitmap font has an invalid '{attribute}' value.");
        return metric;
    }
    private static float GetTextSize(JsonObject command)
    {
        float size = LegacySaveDocument.ReadFloat(command["textSize"], 0f);
        return size > 0f ? size : DefaultTextSize;
    }


    private static Color DecodeColor(int value, float opacity) => new(
        ((value >> 16) & 0xff) / 255f,
        ((value >> 8) & 0xff) / 255f,
        (value & 0xff) / 255f,
        opacity);
}
