using System.Text.Json;
using Godot;

namespace GBNG.Assets;

public sealed class LegacyRenderableCatalog
{
    private readonly Dictionary<string, RenderableEntry> _entries = new(StringComparer.Ordinal);

    private LegacyRenderableCatalog() { }
    public IReadOnlyCollection<RenderableEntry> Entries => _entries.Values;

    public static LegacyRenderableCatalog Load(string path = "res://Godot/Generated/renderables.json")
    {
        string json = FileAccess.GetFileAsString(path);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException($"The generated renderable catalog is missing or empty: {path}");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("formatVersion", out JsonElement version) || version.GetInt32() != 1)
            throw new InvalidDataException("Unsupported generated renderable catalog format.");
        if (!root.TryGetProperty("renderables", out JsonElement renderables) || renderables.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Generated renderable catalog has no renderables object.");

        var catalog = new LegacyRenderableCatalog();
        foreach (JsonProperty item in renderables.EnumerateObject())
        {
            JsonElement value = item.Value;
            catalog._entries.Add(item.Name, new RenderableEntry(
                item.Name,
                GetString(value, "prefab"),
                GetStringArray(value, "models"),
                GetStringArray(value, "images")));
        }
        return catalog;
    }

    public bool TryGet(string uri, out RenderableEntry entry) => _entries.TryGetValue(uri, out entry!);

    private static string GetString(JsonElement value, string propertyName)
    {
        return value.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string[] GetStringArray(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }
}

public sealed record RenderableEntry(string Uri, string PrefabPath, string[] ModelPaths, string[] ImagePaths)
{
    public string? SelectPrimaryModel()
    {
        if (ModelPaths.Length == 0)
            return null;

        string expectedName = NormalizeName(Uri[(Uri.LastIndexOf('/') + 1)..]);
        string? bestPath = null;
        int bestScore = -1;
        foreach (string path in ModelPaths)
        {
            string modelName = NormalizeName(Path.GetFileNameWithoutExtension(path));
            int score = modelName.Equals(expectedName, StringComparison.Ordinal) ? 100
                : modelName.StartsWith(expectedName, StringComparison.Ordinal) ? 80
                : expectedName.StartsWith(modelName, StringComparison.Ordinal) ? 60
                : modelName.Contains(expectedName, StringComparison.Ordinal) ? 40
                : 0;
            if (score > bestScore)
            {
                bestScore = score;
                bestPath = path;
            }
        }

        return bestScore > 0 ? bestPath : ModelPaths[0];
    }

    private static string NormalizeName(string name)
    {
        return new string(name.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
