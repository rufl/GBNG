using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Godot;

namespace GBNG.App;

public sealed partial class LegacyJavaScriptCodeEdit : CodeEdit
{
    private static readonly Regex GlobalFunctionDeclaration = new(
        @"(?m)^(?:async\s+)?function\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DiagnosticColumn = new(
        @"(?:^Line \d+: Column |line \d+:)(?<column>\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] ApiFunctionNames = LoadApiFunctionNames();
    private const int MinimumFontSize = 8;
    private const int MaximumFontSize = 36;
    private const int FontSizeStep = 2;
    public const string DiagnosticGutterName = "JavaScriptDiagnostics";
    private static readonly Color DiagnosticLineBackground = new(0.48f, 0.08f, 0.08f, 0.28f);
    private static readonly Color DiagnosticGutterColor = new(1f, 0.36f, 0.36f);
    private static readonly Color DiagnosticUnderlineColor = new(1f, 0.24f, 0.24f);
    private readonly Dictionary<int, (int StartColumn, int EndColumn)> _diagnosticUnderlineRanges = new();
    private readonly Dictionary<int, string> _diagnosticMessagesByLine = new();
    private int _diagnosticGutter = -1;

    public LegacyJavaScriptCodeEdit()
    {
        GuttersDrawLineNumbers = true;
        IndentAutomatic = true;
        IndentSize = 2;
        IndentUseSpaces = true;
        AutoBraceCompletionEnabled = true;
        AutoBraceCompletionHighlightMatching = true;
        LineFolding = true;
        CodeCompletionEnabled = true;
        SyntaxHighlighter = CreateHighlighter();
        AddCommentDelimiter("//", "", true);
        AddCommentDelimiter("/*", "*/");
        AddStringDelimiter("`", "`");
        CodeCompletionRequested += PopulateApiCompletions;
        _diagnosticGutter = GetGutterCount();
        AddGutter();
        SetGutterName(_diagnosticGutter, DiagnosticGutterName);
        SetGutterType(_diagnosticGutter, GutterType.String);
        SetGutterWidth(_diagnosticGutter, 0);
        SetGutterDraw(_diagnosticGutter, true);
    }

    public void ZoomIn() => AdjustFontSize(FontSizeStep);

    public void ZoomOut() => AdjustFontSize(-FontSizeStep);

    public void ResetZoom() => RemoveThemeFontSizeOverride("font_size");

    public void AddDiagnosticLine(int oneBasedLineNumber, string message)
    {
        int line = oneBasedLineNumber - 1;
        if (line < 0 || line >= GetLineCount())
            return;

        _diagnosticMessagesByLine[line] = message;
        Match columnMatch = DiagnosticColumn.Match(message);
        if (columnMatch.Success
            && int.TryParse(columnMatch.Groups["column"].Value, out int oneBasedColumn)
            && oneBasedColumn > 0)
            _diagnosticUnderlineRanges[line] = FindDiagnosticUnderlineRange(line, oneBasedColumn);
        else
            _diagnosticUnderlineRanges.Remove(line);
        SetGutterWidth(_diagnosticGutter, 16);
        SetLineGutterText(line, _diagnosticGutter, "!");
        SetLineGutterItemColor(line, _diagnosticGutter, DiagnosticGutterColor);
        SetLineBackgroundColor(line, DiagnosticLineBackground);
        QueueRedraw();
    }

    public void ClearDiagnosticLines()
    {
        foreach (int line in _diagnosticMessagesByLine.Keys)
        {
            if (line >= GetLineCount())
                continue;
            SetLineGutterText(line, _diagnosticGutter, "");
            SetLineGutterItemColor(line, _diagnosticGutter, Colors.Transparent);
            SetLineBackgroundColor(line, Colors.Transparent);
        }
        _diagnosticMessagesByLine.Clear();
        _diagnosticUnderlineRanges.Clear();
        SetGutterWidth(_diagnosticGutter, 0);
        QueueRedraw();
    }

    public override void _Draw()
    {
        foreach (KeyValuePair<int, (int StartColumn, int EndColumn)> diagnostic in _diagnosticUnderlineRanges)
        {
            int line = diagnostic.Key;
            if (line >= GetLineCount())
                continue;

            Rect2I startRect = GetRectAtLineColumn(line, diagnostic.Value.StartColumn);
            Rect2I endRect = GetRectAtLineColumn(line, diagnostic.Value.EndColumn);
            float startX = startRect.Position.X;
            float endX = endRect.Position.X;
            if (endX - startX < 5f)
                endX = startX + Mathf.Max(startRect.Size.X, 5f);
            float baseline = startRect.Position.Y + startRect.Size.Y - 1f;
            if (baseline < 0f || baseline > Size.Y)
                continue;
            DrawDiagnosticSquiggle(startX, endX, baseline);
        }
    }

    private (int StartColumn, int EndColumn) FindDiagnosticUnderlineRange(int line, int oneBasedColumn)
    {
        string sourceLine = GetLine(line);
        int startColumn = Math.Clamp(oneBasedColumn - 1, 0, sourceLine.Length);
        while (startColumn < sourceLine.Length && char.IsWhiteSpace(sourceLine[startColumn]))
            startColumn++;
        if (startColumn == sourceLine.Length)
            return (startColumn, startColumn);

        int endColumn = startColumn + 1;
        if (IsIdentifierCharacter(sourceLine[startColumn]))
        {
            while (endColumn < sourceLine.Length && IsIdentifierCharacter(sourceLine[endColumn]))
                endColumn++;
        }
        return (startColumn, endColumn);
    }

    private void DrawDiagnosticSquiggle(float startX, float endX, float baseline)
    {
        const float segmentWidth = 2f;
        const float amplitude = 1.25f;
        float x = startX;
        float y = baseline;
        bool crest = false;
        while (x < endX)
        {
            float nextX = Mathf.Min(x + segmentWidth, endX);
            float nextY = baseline + (crest ? -amplitude : amplitude);
            DrawLine(new Vector2(x, y), new Vector2(nextX, nextY), DiagnosticUnderlineColor, 1.5f, true);
            x = nextX;
            y = nextY;
            crest = !crest;
        }
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        if (atPosition.X < 0f || atPosition.Y < 0f || atPosition.X >= Size.X || atPosition.Y >= Size.Y)
            return string.Empty;

        Vector2I lineAndColumn = GetLineColumnAtPos(
            new Vector2I((int)atPosition.X, (int)atPosition.Y),
            false,
            true);
        return _diagnosticMessagesByLine.TryGetValue(lineAndColumn.Y, out string? message)
            ? message
            : string.Empty;
    }

    private void AdjustFontSize(int delta)
    {
        int fontSize = Mathf.Clamp(GetThemeFontSize("font_size") + delta, MinimumFontSize, MaximumFontSize);
        AddThemeFontSizeOverride("font_size", fontSize);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Keycode: Key.Space, Pressed: true, Echo: false, CtrlPressed: true })
        {
            RequestCodeCompletion(true);
            AcceptEvent();
            return;
        }

        base._GuiInput(@event);
    }

    private void PopulateApiCompletions()
    {
        string text = GetTextForCodeCompletion();
        int caret = text.IndexOf('\uffff');
        if (caret < 0)
            caret = text.Length;
        int prefixStart = caret;
        while (prefixStart > 0 && IsIdentifierCharacter(text[prefixStart - 1]))
            prefixStart--;
        string prefix = text[prefixStart..caret];
        if (text[..caret].TrimEnd().EndsWith(".", StringComparison.Ordinal))
        {
            UpdateCodeCompletionOptions(true);
            return;
        }

        foreach (string name in ApiFunctionNames)
        {
            if (prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            AddCodeCompletionOption(CodeCompletionKind.Function, name, $"{name}()");
        }
        UpdateCodeCompletionOptions(true);
    }

    private static bool IsIdentifierCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '$';

    private static string[] LoadApiFunctionNames()
    {
        string apiDirectory = ProjectSettings.GlobalizePath("res://Assets/Scripts/Behaviors/JavaScript/apiv2");
        if (!Directory.Exists(apiDirectory))
            return [];

        var names = new SortedSet<string>(System.StringComparer.Ordinal);
        foreach (string filePath in Directory.EnumerateFiles(apiDirectory, "*.js.txt", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(filePath);
            if (!source.Contains("// VISIBLE_TO_MONACO", System.StringComparison.Ordinal))
                continue;
            foreach (Match match in GlobalFunctionDeclaration.Matches(source))
                names.Add(match.Groups[1].Value);
        }
        return [.. names];
    }

    private static CodeHighlighter CreateHighlighter()
    {
        var highlighter = new CodeHighlighter
        {
            FunctionColor = new Color(0.45f, 0.78f, 0.98f),
            NumberColor = new Color(0.95f, 0.72f, 0.47f),
            SymbolColor = new Color(0.82f, 0.86f, 0.92f)
        };
        Color keywordColor = new(0.79f, 0.62f, 0.98f);
        foreach (string keyword in new[]
        {
            "async", "await", "break", "case", "catch", "class", "const", "continue", "default",
            "delete", "do", "else", "export", "extends", "false", "finally", "for", "from",
            "function", "if", "import", "in", "instanceof", "let", "new", "null", "of", "return",
            "static", "super", "switch", "this", "throw", "true", "try", "typeof", "undefined",
            "var", "void", "while", "yield"
        })
            highlighter.AddKeywordColor(keyword, keywordColor);
        foreach (string functionName in ApiFunctionNames)
            highlighter.AddKeywordColor(functionName, new Color(0.46f, 0.86f, 0.74f));

        highlighter.AddColorRegion("//", "", new Color(0.55f, 0.68f, 0.57f), true);
        highlighter.AddColorRegion("/*", "*/", new Color(0.55f, 0.68f, 0.57f));
        highlighter.AddColorRegion("'", "'", new Color(0.93f, 0.71f, 0.55f));
        highlighter.AddColorRegion("\"", "\"", new Color(0.93f, 0.71f, 0.55f));
        highlighter.AddColorRegion("`", "`", new Color(0.93f, 0.71f, 0.55f));
        return highlighter;
    }
}
