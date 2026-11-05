using System.Globalization;
using System.Text;
using GBNG.Domain;
using Godot;

namespace GBNG.World;

public partial class LegacyCommentPanelRenderer : Node3D
{
    private const int BaseFontSize = 32;
    private const int HorizontalMargin = 26;
    private const int VerticalMargin = 26;
    private const int PixelsPerWorldUnit = 512;
    private const int MinimumTextureWidth = 128;
    private const int MaximumTextureWidth = 1536;
    private const int MaximumTextureHeight = 4096;
    private const float PanelOpacity = 0.749f;

    private SubViewport? _textViewport;
    private RichTextLabel? _textLabel;
    private QuadMesh? _panelQuad;
    private StandardMaterial3D? _panelMaterial;
    private Color _tint = Colors.White;
    private string _commentText = string.Empty;

    public SubViewport? TextViewport => _textViewport;
    public string CommentText => _commentText;

    public static bool SupportsRenderable(string renderableUri)
        => renderableUri is "builtin:BuiltinAssets/CommentPanel" or "builtin:BuiltinAssets/BillboardTextPanel";

    public override void _Ready()
    {
        _textViewport = new SubViewport
        {
            Name = "CommentTextViewport",
            Size = new Vector2I(MinimumTextureWidth, MinimumTextureWidth),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once
        };
        AddChild(_textViewport);

        _textLabel = new RichTextLabel
        {
            Name = "CommentText",
            BbcodeEnabled = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _textLabel.AddThemeFontSizeOverride("normal_font_size", BaseFontSize);
        _textLabel.AddThemeConstantOverride("line_separation", 2);
        _textLabel.AddThemeColorOverride("default_color", Colors.White);
        _textViewport.AddChild(_textLabel);

        _panelQuad = new QuadMesh { Size = Vector2.One };
        _panelMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0f, 0f, 0f, PanelOpacity),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };

        var background = new MeshInstance3D
        {
            Name = "CommentPanelBackground",
            Mesh = _panelQuad,
            MaterialOverride = _panelMaterial,
            Rotation = new Vector3(0f, Mathf.Pi, 0f)
        };
        AddChild(background);

        var textMaterial = new StandardMaterial3D
        {
            AlbedoTexture = _textViewport.GetTexture(),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        var textSurface = new MeshInstance3D
        {
            Name = "CommentPanelTextSurface",
            Mesh = _panelQuad,
            MaterialOverride = textMaterial,
            Position = new Vector3(0f, 0f, -0.003f),
            Rotation = new Vector3(0f, Mathf.Pi, 0f)
        };
        AddChild(textSurface);

        SetCommentText(_commentText);
        SetTint(_tint);
    }

    public void SetCommentText(string text)
    {
        _commentText = text ?? string.Empty;
        RefreshText();
    }

    public void SetTint(Color tint)
    {
        _tint = tint;
        if (_panelMaterial is not null)
            _panelMaterial.AlbedoColor = tint;
    }

    private void RefreshText()
    {
        if (_textViewport is null || _textLabel is null || _panelQuad is null)
            return;

        string bbcode = ConvertMarkup(_commentText, out string visibleText);
        Vector2I size = CalculateTextureSize(visibleText);
        _textViewport.Size = size;
        _textLabel.Position = new Vector2(HorizontalMargin, VerticalMargin);
        _textLabel.Size = new Vector2(
            Math.Max(1, size.X - HorizontalMargin * 2),
            Math.Max(1, size.Y - VerticalMargin * 2));
        _textLabel.Text = bbcode;
        _textViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        _panelQuad.Size = new Vector2(
            size.X / (float)PixelsPerWorldUnit,
            size.Y / (float)PixelsPerWorldUnit);
    }

    private static Vector2I CalculateTextureSize(string visibleText)
    {
        string[] lines = visibleText.Split('\n');
        double widestLine = lines.Max(line => line.Length * BaseFontSize * 0.56d);
        int width = Math.Clamp(
            (int)Math.Min(MaximumTextureWidth, Math.Ceiling(widestLine) + HorizontalMargin * 2),
            MinimumTextureWidth,
            MaximumTextureWidth);
        int glyphWidth = Math.Max(1, (int)Math.Ceiling(BaseFontSize * 0.56f));
        int charactersPerLine = Math.Max(1, (width - HorizontalMargin * 2) / glyphWidth);
        long wrappedLines = 0;
        foreach (string line in lines)
        {
            long lineCount = Math.Max(1L, ((long)line.Length + charactersPerLine - 1) / charactersPerLine);
            wrappedLines = Math.Min(MaximumTextureHeight, wrappedLines + lineCount);
        }

        int lineHeight = (int)Math.Ceiling(BaseFontSize * 1.25f) + 2;
        int height = (int)Math.Clamp(
            wrappedLines * lineHeight + VerticalMargin * 2L,
            MinimumTextureWidth,
            MaximumTextureHeight);
        return new Vector2I(width, height);
    }

    private static string ConvertMarkup(string text, out string visibleText)
    {
        var bbcode = new StringBuilder(text.Length + 16);
        var visible = new StringBuilder(text.Length);
        for (int index = 0; index < text.Length;)
        {
            if (text[index] == '<')
            {
                int tagEnd = text.IndexOf('>', index + 1);
                if (tagEnd > index && TryAppendMarkupTag(text.AsSpan(index + 1, tagEnd - index - 1), bbcode, visible))
                {
                    index = tagEnd + 1;
                    continue;
                }
            }

            char character = text[index++];
            if (character == '\r')
            {
                if (index < text.Length && text[index] == '\n')
                    index++;
                character = '\n';
            }
            visible.Append(character);
            if (character == '[')
                bbcode.Append("[[");
            else
                bbcode.Append(character);
        }

        visibleText = visible.ToString();
        return bbcode.ToString();
    }

    private static bool TryAppendMarkupTag(ReadOnlySpan<char> source, StringBuilder bbcode, StringBuilder visible)
    {
        string tag = source.ToString().Trim();
        bool closing = tag.StartsWith("/", StringComparison.Ordinal);
        if (closing)
            tag = tag[1..].TrimStart();
        if (tag.EndsWith("/", StringComparison.Ordinal))
            tag = tag[..^1].TrimEnd();

        int equalsIndex = tag.IndexOf('=');
        string name = (equalsIndex < 0 ? tag : tag[..equalsIndex]).Trim().ToLowerInvariant();
        string value = equalsIndex < 0 ? string.Empty : tag[(equalsIndex + 1)..].Trim();
        switch (name)
        {
            case "br" when !closing:
                bbcode.Append('\n');
                visible.Append('\n');
                return true;
            case "b": return AppendSimpleTag("b", closing, bbcode);
            case "i": return AppendSimpleTag("i", closing, bbcode);
            case "u": return AppendSimpleTag("u", closing, bbcode);
            case "s": return AppendSimpleTag("s", closing, bbcode);
            case "color" when closing:
                bbcode.Append("[/color]");
                return true;
            case "color" when IsSafeMarkupValue(value):
                bbcode.Append("[color=").Append(value).Append(']');
                return true;
            case "size" when closing:
                bbcode.Append("[/font_size]");
                return true;
            case "size" when TryConvertFontSize(value, out int fontSize):
                bbcode.Append("[font_size=").Append(fontSize).Append(']');
                return true;
            case "mark" when closing:
                bbcode.Append("[/bgcolor]");
                return true;
            case "mark" when value.Length == 0:
                bbcode.Append("[bgcolor=#ffff00]");
                return true;
            case "mark" when IsSafeMarkupValue(value):
                bbcode.Append("[bgcolor=").Append(value).Append(']');
                return true;
            default:
                return false;
        }
    }

    private static bool AppendSimpleTag(string name, bool closing, StringBuilder bbcode)
    {
        bbcode.Append('[').Append(closing ? "/" : string.Empty).Append(name).Append(']');
        return true;
    }

    private static bool IsSafeMarkupValue(string value)
        => value.Length is > 0 and <= 32
            && value.All(character => char.IsLetterOrDigit(character) || character is '#' or '.' or ',' or '%');

    private static bool TryConvertFontSize(string value, out int fontSize)
    {
        fontSize = BaseFontSize;
        bool isPercentage = value.EndsWith('%');
        string number = isPercentage ? value[..^1] : value;
        if (!float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            || !float.IsFinite(parsed))
            return false;

        float pixels = isPercentage ? BaseFontSize * parsed / 100f : parsed;
        if (pixels < 4f || pixels > 256f)
            return false;
        fontSize = Mathf.RoundToInt(pixels);
        return true;
    }
}
