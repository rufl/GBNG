using System.Text.Json.Nodes;
using GBNG.Domain;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunTerrainStyleSmokeStandalone()
    {
        int exitCode = 0;
        try
        {
            await WaitForUiFrames(1);
            if (_document is null)
                throw new InvalidOperationException("The sample project did not load for the terrain style smoke run.");
            await RunTerrainStyleSmoke();
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"TERRAIN_STYLE_SMOKE_FAILED: {exception}");
        }
        finally
        {
            GetTree().Quit(exitCode);
        }
    }

    private async System.Threading.Tasks.Task RunTerrainStyleSmoke()
    {
        string projectPath = ProjectSettings.GlobalizePath("res://.godot/terrain-style-smoke.voos");
        _document!.SaveCopyTo(projectPath);
        LoadProject(projectPath);
        await WaitForUiFrames(3);

        LegacySaveDocument document = _document
            ?? throw new InvalidOperationException("The terrain style smoke project did not reload.");
        LegacyTerrainNode terrain = _terrain
            ?? throw new InvalidOperationException("The terrain style smoke project has no terrain grid.");
        JsonObject gridState = LegacySaveDocument.EnsureObject(document.Root, "gridState");
        JsonObject metadata = LegacySaveDocument.EnsureObject(gridState, "metadata");
        metadata["customStyleWorkshopIds"] = new JsonArray(JsonValue.Create(123456));
        if (!terrain.SetCell(4, 20, 4, 0, LegacyTerrainStyles.FirstCustomStyle, 0))
            throw new InvalidOperationException("Could not create the missing Workshop style fixture cell.");
        terrain.FlushUpdates();

        OpenTerrainEditor();
        await WaitForUiFrames(2);
        LegacyTerrainEditorWindow window = _terrainEditorWindow
            ?? throw new InvalidOperationException("The terrain editor did not open for the style smoke.");
        window.SelectStyle(LegacyTerrainStyles.FirstCustomStyle);
        string replacementTexturePath = ProjectSettings.GlobalizePath("res://.godot/terrain-style-restore.png");
        WriteTerrainStyleTexture(replacementTexturePath, new Color("#e3432f"));
        _pendingTerrainStyleImport = true;
        _pendingTerrainStyleReplacementId = window.Brush.Style;
        ImportImageFromPath(replacementTexturePath);

        LegacyTerrainStyleOption restoredStyle = LegacyTerrainStyles.Read(document.GridState, terrain.Data)
            .Single(style => style.Id == LegacyTerrainStyles.FirstCustomStyle && style.AssetUri is not null);
        if (!terrain.TryGetCell(4, 20, 4, out _, out ushort restoredCellStyle, out _)
            || restoredCellStyle != restoredStyle.Id)
            throw new InvalidOperationException("Replacing a missing Workshop texture changed the saved terrain style ID.");
        AssertTerrainStyleTexture(terrain, restoredStyle.Id, new Color("#e3432f"));

        window.SelectStyle(16);
        string addedTexturePath = ProjectSettings.GlobalizePath("res://.godot/terrain-style-new.png");
        WriteTerrainStyleTexture(addedTexturePath, new Color("#38a568"));
        _pendingTerrainStyleImport = true;
        _pendingTerrainStyleReplacementId = null;
        ImportImageFromPath(addedTexturePath);
        LegacyTerrainStyleOption addedStyle = LegacyTerrainStyles.Read(document.GridState, terrain.Data)
            .Single(style => style.Name == "terrain-style-new" && style.AssetUri is not null);
        if (addedStyle.Id == restoredStyle.Id
            || !document.GridState!["metadata"]!["localStyleAssets"]!.AsArray().Any(node =>
                LegacySaveDocument.ReadInt((node as JsonObject)?["style"], -1) == addedStyle.Id))
            throw new InvalidOperationException("Importing a new terrain texture did not allocate and persist a separate style.");
        if (!terrain.SetCell(5, 20, 5, 0, addedStyle.Id, 0))
            throw new InvalidOperationException("Could not create a terrain cell using the newly imported style.");
        terrain.FlushUpdates();
        AssertTerrainStyleTexture(terrain, addedStyle.Id, new Color("#38a568"));

        document.SaveTerrainPayload(terrain.Encode());
        document.SaveTo(projectPath);
        LoadProject(projectPath);
        await WaitForUiFrames(3);

        document = _document
            ?? throw new InvalidOperationException("The saved terrain style project did not reload.");
        terrain = _terrain
            ?? throw new InvalidOperationException("The saved terrain style project lost its terrain grid.");
        if (!terrain.TryGetCell(4, 20, 4, out _, out ushort loadedRestoredStyle, out _)
            || loadedRestoredStyle != restoredStyle.Id
            || !terrain.TryGetCell(5, 20, 5, out _, out ushort loadedAddedStyle, out _)
            || loadedAddedStyle != addedStyle.Id)
            throw new InvalidOperationException("Terrain style IDs did not survive saving and reloading the .voos project.");
        AssertTerrainStyleTexture(terrain, restoredStyle.Id, new Color("#e3432f"));
        AssertTerrainStyleTexture(terrain, addedStyle.Id, new Color("#38a568"));
        OpenTerrainEditor();
        await WaitForUiFrames(2);
        if (_terrainEditorWindow is null || !_terrainEditorWindow.Visible)
            throw new InvalidOperationException("The terrain editor did not remain available after project reload.");
        string screenshot = ProjectSettings.GlobalizePath("res://.godot/terrain-style-smoke.png");
        if (_terrainEditorWindow.GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
            throw new InvalidOperationException("Could not save the custom terrain style editor screenshot.");
        GD.Print($"TERRAIN_STYLE_SMOKE_OK replacedStyle={restoredStyle.Id} addedStyle={addedStyle.Id} persisted=true localImages=true screenshot={screenshot}");
    }

    private static void WriteTerrainStyleTexture(string path, Color color)
    {
        var image = Image.CreateEmpty(8, 8, useMipmaps: false, Image.Format.Rgba8);
        image.Fill(color);
        if (image.SavePng(path) != Error.Ok)
            throw new InvalidOperationException($"Could not write terrain style smoke image: {path}");
    }

    private static void AssertTerrainStyleTexture(LegacyTerrainNode terrain, ushort styleId, Color expectedColor)
    {
        if (terrain.GetNode<MeshInstance3D>("TerrainMesh").Mesh is not ArrayMesh mesh)
            throw new InvalidOperationException("The terrain mesh is unavailable while checking its imported style.");
        for (int index = 0; index < mesh.GetSurfaceCount(); index++)
        {
            if (mesh.SurfaceGetMaterial(index) is not StandardMaterial3D material
                || material.AlbedoTexture is not Texture2D texture)
                continue;
            Image image = texture.GetImage();
            if (image.IsEmpty())
                continue;
            Color actual = image.GetPixel(0, 0);
            if (Mathf.Abs(actual.R - expectedColor.R) < 0.03f
                && Mathf.Abs(actual.G - expectedColor.G) < 0.03f
                && Mathf.Abs(actual.B - expectedColor.B) < 0.03f)
                return;
        }
        throw new InvalidOperationException($"No terrain material rendered the imported texture for style {styleId}.");
    }
}
