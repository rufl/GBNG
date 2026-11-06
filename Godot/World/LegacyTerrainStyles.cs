using System.Text.Json.Nodes;
using GBNG.Assets;
using GBNG.Domain;
using Godot;

namespace GBNG.World;

public sealed record LegacyTerrainStyleOption(ushort Id, string Name, string? AssetUri);

public static class LegacyTerrainStyles
{
    public const ushort FirstCustomStyle = 256;
    public const ushort LastCustomStyle = 511;
    private const string LocalStylesProperty = "localStyleAssets";

    public static IReadOnlyList<LegacyTerrainStyleOption> Read(JsonObject? gridState, LegacyTerrainData? terrainData = null)
    {
        var styles = new Dictionary<ushort, LegacyTerrainStyleOption>();
        JsonObject? metadata = gridState?["metadata"] as JsonObject;
        if (metadata?[LocalStylesProperty] is JsonArray localStyles)
        {
            foreach (JsonNode? node in localStyles)
            {
                if (node is not JsonObject styleNode)
                    continue;
                int id = LegacySaveDocument.ReadInt(styleNode["style"], -1);
                if (id < FirstCustomStyle || id > LastCustomStyle)
                    continue;
                string uri = LegacySaveDocument.ReadString(styleNode["uri"]);
                string name = LegacySaveDocument.ReadString(styleNode["name"], $"Terrain style {id}");
                styles[(ushort)id] = new LegacyTerrainStyleOption((ushort)id, name, uri.Length == 0 ? null : uri);
            }
        }

        if (metadata?["customStyleWorkshopIds"] is JsonArray workshopStyles)
        {
            int count = Math.Min(workshopStyles.Count, LastCustomStyle - FirstCustomStyle + 1);
            for (int index = 0; index < count; index++)
            {
                ushort id = (ushort)(FirstCustomStyle + index);
                styles.TryAdd(id, new LegacyTerrainStyleOption(id, $"Workshop style {index + 1} (offline)", null));
            }
        }

        if (terrainData is not null)
        {
            foreach (TerrainCell cell in terrainData.EnumerateCells())
            {
                if (cell.Style >= FirstCustomStyle && !styles.ContainsKey(cell.Style))
                    styles.Add(cell.Style, new LegacyTerrainStyleOption(cell.Style, $"Missing style {cell.Style}", null));
            }
        }

        return styles.Values.OrderBy(style => style.Id).ToArray();
    }

    public static LegacyTerrainStyleOption AddLocalStyle(
        JsonObject projectRoot,
        LegacyTerrainData? terrainData,
        string uri,
        string name,
        int? replaceStyleId = null)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("A project image asset URI is required.", nameof(uri));

        JsonObject gridState = LegacySaveDocument.EnsureObject(projectRoot, "gridState");
        JsonObject metadata = LegacySaveDocument.EnsureObject(gridState, "metadata");
        JsonArray localStyles;
        if (metadata[LocalStylesProperty] is JsonArray existing)
        {
            localStyles = existing;
        }
        else
        {
            localStyles = new JsonArray();
            metadata[LocalStylesProperty] = localStyles;
        }

        ushort styleId = replaceStyleId is >= FirstCustomStyle and <= LastCustomStyle
            ? (ushort)replaceStyleId.Value
            : AllocateStyleId(gridState, terrainData);
        string normalizedName = string.IsNullOrWhiteSpace(name) ? $"Terrain style {styleId}" : name.Trim();
        var styleNode = new JsonObject
        {
            ["style"] = (int)styleId,
            ["name"] = normalizedName,
            ["uri"] = uri
        };

        for (int index = 0; index < localStyles.Count; index++)
        {
            if (localStyles[index] is JsonObject existingStyle
                && LegacySaveDocument.ReadInt(existingStyle["style"], -1) == styleId)
            {
                localStyles[index] = styleNode;
                return new LegacyTerrainStyleOption(styleId, normalizedName, uri);
            }
        }

        localStyles.Add(styleNode);
        return new LegacyTerrainStyleOption(styleId, normalizedName, uri);
    }

    public static Dictionary<ushort, Texture2D> LoadTextures(JsonObject? gridState, LegacyRenderableCatalog catalog)
    {
        var textures = new Dictionary<ushort, Texture2D>();
        foreach (LegacyTerrainStyleOption style in Read(gridState))
        {
            if (style.AssetUri is null)
                continue;
            if (!catalog.TryGet(style.AssetUri, out RenderableEntry entry)
                || entry.ImagePaths.Length == 0
                || !File.Exists(entry.ImagePaths[0]))
            {
                GD.PushWarning($"Terrain style '{style.Name}' refers to an unavailable local image asset: {style.AssetUri}.");
                continue;
            }

            Image image = Image.LoadFromFile(entry.ImagePaths[0]);
            if (image.IsEmpty())
            {
                GD.PushWarning($"Terrain style '{style.Name}' has an unreadable image asset: {entry.ImagePaths[0]}.");
                continue;
            }

            textures.Add(style.Id, ImageTexture.CreateFromImage(image));
        }
        return textures;
    }

    private static ushort AllocateStyleId(JsonObject gridState, LegacyTerrainData? terrainData)
    {
        var occupied = Read(gridState, terrainData).Select(style => style.Id).ToHashSet();
        for (int style = FirstCustomStyle; style <= LastCustomStyle; style++)
        {
            ushort id = (ushort)style;
            if (!occupied.Contains(id))
                return id;
        }
        throw new InvalidOperationException("All 256 custom terrain style slots are in use.");
    }
}
