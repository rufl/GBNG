using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using GBNG.Assets;
using GBNG.Domain;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async System.Threading.Tasks.Task RunTerrainEditorSmoke()
    {
        LegacyTerrainNode terrain = _terrain
            ?? throw new InvalidOperationException("The sample project did not load its legacy terrain.");
        if (!terrain.TryGetCell(100, 19, 100, out byte originalShape, out ushort originalStyle, out byte originalDirection)
            || originalShape != 0
            || originalStyle != 27)
            throw new InvalidOperationException("The sample's interior terrain test cell changed.");

        Transform3D oldCameraTransform = _camera.GlobalTransform;
        var target = new Vector3(
            0f,
            (19 + LegacyTerrainData.CellsYStart) * LegacyTerrainData.CellHeight + LegacyTerrainData.CellHeight * 0.5f,
            0f);
        _camera.GlobalPosition = target + Vector3.Up * 12f;
        _camera.LookAt(target, Vector3.Forward);
        OpenTerrainEditor();
        await WaitForUiFrames(3);

        LegacyTerrainEditorWindow window = _terrainEditorWindow
            ?? throw new InvalidOperationException("The terrain editor window did not open.");
        if (!window.Visible)
            throw new InvalidOperationException("The terrain editor window is not visible.");
        window.SetBrushActive(true);
        Vector2 center = GetViewport().GetVisibleRect().Size * 0.5f;
        await WaitForUiFrames(2);

        OptionButton mode = (OptionButton)window.FindChild("TerrainMode", true, false);
        OptionButton shape = (OptionButton)window.FindChild("TerrainShape", true, false);
        OptionButton direction = (OptionButton)window.FindChild("TerrainDirection", true, false);
        OptionButton style = (OptionButton)window.FindChild("TerrainStyle", true, false);
        mode.Select((int)LegacyTerrainBrushMode.Dig);
        ApplyTerrainSmokeStroke(center);
        if (terrain.TryGetCell(100, 19, 100, out _, out _, out _))
            throw new InvalidOperationException("The dig brush did not remove the targeted .voos terrain cell.");
        UndoEdit();
        await WaitForUiFrames(2);
        if (!terrain.TryGetCell(100, 19, 100, out byte restoredShape, out ushort restoredStyle, out byte restoredDirection)
            || restoredShape != originalShape || restoredStyle != originalStyle || restoredDirection != originalDirection)
            throw new InvalidOperationException("Terrain undo did not restore shape, style, and direction.");
        RedoEdit();
        await WaitForUiFrames(2);
        if (terrain.TryGetCell(100, 19, 100, out _, out _, out _))
            throw new InvalidOperationException("Terrain redo did not reapply the dig stroke.");
        UndoEdit();
        await WaitForUiFrames(2);

        mode.Select((int)LegacyTerrainBrushMode.Paint);
        style.Select(24);
        ApplyTerrainSmokeStroke(center);
        if (!terrain.TryGetCell(100, 19, 100, out byte paintedShape, out ushort paintedStyle, out byte paintedDirection)
            || paintedShape != originalShape || paintedStyle != 24 || paintedDirection != originalDirection)
            throw new InvalidOperationException("The paint brush did not preserve the cell shape and direction while changing style.");
        UndoEdit();
        await WaitForUiFrames(2);
        if (!terrain.TryGetCell(100, 19, 100, out _, out ushort undoneStyle, out _) || undoneStyle != originalStyle)
            throw new InvalidOperationException("Paint undo did not restore the original terrain style.");
        RedoEdit();
        await WaitForUiFrames(2);
        if (!terrain.TryGetCell(100, 19, 100, out _, out ushort redoneStyle, out _) || redoneStyle != 24)
            throw new InvalidOperationException("Paint redo did not restore the painted terrain style.");
        UndoEdit();
        await WaitForUiFrames(2);

        mode.Select((int)LegacyTerrainBrushMode.Create);
        shape.Select(3);
        direction.Select(2);
        style.Select(25);
        ApplyTerrainSmokeStroke(center);
        if (!terrain.TryGetCell(100, 20, 100, out byte createdShape, out ushort createdStyle, out byte createdDirection)
            || createdShape != 3 || createdStyle != 25 || createdDirection != 2)
            throw new InvalidOperationException("The create brush did not add the configured adjacent .voos cell.");
        UndoEdit();
        await WaitForUiFrames(2);
        if (terrain.TryGetCell(100, 20, 100, out _, out _, out _))
            throw new InvalidOperationException("Terrain creation undo did not remove its cell.");
        RedoEdit();
        await WaitForUiFrames(2);
        if (!terrain.TryGetCell(100, 20, 100, out _, out _, out _))
            throw new InvalidOperationException("Terrain creation redo did not restore its cell.");
        UndoEdit();
        await WaitForUiFrames(2);

        _document!.SaveTerrainPayload(terrain.Encode());
        LegacyTerrainData? roundTrip = LegacyTerrainData.Decode(_document.GridState);
        if (roundTrip is null
            || !roundTrip.TryGetCell(100, 19, 100, out byte savedShape, out ushort savedStyle, out byte savedDirection)
            || savedShape != originalShape || savedStyle != originalStyle || savedDirection != originalDirection
            || roundTrip.TryGetCell(100, 20, 100, out _, out _, out _))
            throw new InvalidOperationException("Terrain edits did not round-trip through the original .voos payload.");

        string screenshot = ProjectSettings.GlobalizePath("res://.godot/terrain-editor-smoke.png");
        if (window.GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
            throw new InvalidOperationException("Could not save the terrain editor screenshot.");
        window.SetBrushActive(false);
        window.Hide();
        _camera.GlobalTransform = oldCameraTransform;
        UpdateOrbitCamera();
        ClearHistory();
        GD.Print($"TERRAIN_EDITOR_SMOKE_OK cell=({100 - terrain.Width / 2},{19 + LegacyTerrainData.CellsYStart},{100 - terrain.Depth / 2}) create=dig=paint undo=redo=.voos-roundtrip screenshot={screenshot}");
    }

    private void ApplyTerrainSmokeStroke(Vector2 screenPosition)
    {
        BeginTerrainStroke();
        ApplyTerrainBrush(screenPosition);
        EndTerrainStroke();
    }

    private async void RunAssetStoreSmoke()
    {
        int exitCode = 0;
        try
        {
            await WaitForUiFrames(2);
            if (_document is null)
                throw new InvalidOperationException("The sample project did not load for the asset-store smoke run.");

            const string expectedUri = "builtin:BuiltinAssets/BubblegumDreams/Arch";
            int originalActorCount = _document.Actors.Count;
            OpenAssetStore();
            await WaitForUiFrames(3);
            if (!_assetStoreWindow.Visible)
                throw new InvalidOperationException("The offline asset store did not open.");

            LineEdit search = (LineEdit)_assetStoreWindow.FindChild("AssetStoreSearch", true, false);
            OptionButton category = (OptionButton)_assetStoreWindow.FindChild("AssetStoreCategory", true, false);
            ItemList assets = (ItemList)_assetStoreWindow.FindChild("AssetStoreItems", true, false);
            Button add = (Button)_assetStoreWindow.FindChild("AssetStoreAddActor", true, false);
            Label license = (Label)_assetStoreWindow.FindChild("AssetStoreLicenseDetails", true, false);
            var preview = (LegacyAssetPreviewControl)_assetStoreWindow.FindChild("LegacyAssetPreview", true, false);

            if (category.GetItemText(category.Selected) != "All categories")
                throw new InvalidOperationException("The asset store did not default to all categories.");

            search.Text = "BubblegumDreams/Arch";
            _assetStoreWindow.RefreshCatalog();
            int[] selected = assets.GetSelectedItems();
            if (assets.ItemCount != 1
                || selected.Length != 1
                || assets.GetItemMetadata(selected[0]).AsString() != expectedUri)
                throw new InvalidOperationException("Asset-store search did not isolate the requested bundled model.");
            if (!preview.Has3DPreview || !license.Text.Contains("Apache License 2.0", StringComparison.Ordinal))
                throw new InvalidOperationException("The bundled model preview or license evidence was not shown.");

            search.Text = "asset-that-does-not-exist";
            _assetStoreWindow.RefreshCatalog();
            if (assets.ItemCount != 0 || !add.Disabled)
                throw new InvalidOperationException("An empty asset search left a stale selection available to add.");

            search.Text = "BubblegumDreams/Arch";
            _assetStoreWindow.RefreshCatalog();
            add.EmitSignal("pressed");
            await WaitForUiFrames(3);
            if (_document.Actors.Count != originalActorCount + 1
                || _document.Actors[^1].RenderableUri != expectedUri)
                throw new InvalidOperationException("Adding the selected store asset did not create the expected actor.");

            await WaitForUiFrames(2);
            string screenshot = ProjectSettings.GlobalizePath("res://.godot/asset-store-smoke.png");
            if (_assetStoreWindow.GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the asset-store screenshot.");
            _assetStoreWindow.Hide();
            GD.Print($"ASSET_STORE_SMOKE_OK uri={expectedUri} search=filtered preview=3d license=shown add=actor screenshot={screenshot}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ASSET_STORE_SMOKE_FAILED: {exception}");
        }
        finally
        {
            GetTree().Quit(exitCode);
        }
    }

    private async void RunCardLibrarySmoke()
    {
        int exitCode = 0;
        try
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_document is null || _behaviorRuntime is null)
                throw new InvalidOperationException("The sample project did not load for the smoke run.");
            await RunTerrainEditorSmoke();

            WorldActorNode? actor = FindActorWithCardDeck();
            if (actor is null)
                throw new InvalidOperationException("The sample has no actor with a card deck.");

            var canvas = new LegacyPanelCanvasWindow(
                _document,
                actor.State,
                () => { },
                uri => _behaviorRuntime.GetBehaviorPropertyDefinitions(uri),
                () => { });
            AddChild(canvas);
            canvas.PopupCentered(new Vector2I(1100, 760));
            await WaitForUiFrames(3);

            Button toggle = (Button)canvas.FindChild("ToggleCardLibrary", true, false);
            toggle.EmitSignal("pressed");
            ItemList items = (ItemList)canvas.FindChild("CardLibraryItems", true, false);
            LineEdit search = (LineEdit)canvas.FindChild("CardLibrarySearch", true, false);
            OptionButton categoryPicker = (OptionButton)canvas.FindChild("CardLibraryCategory", true, false);
            if (items.ItemCount == 0)
                throw new InvalidOperationException("The searchable card library rendered no choices.");

            string firstChoice = items.GetItemMetadata(0).AsString();
            string filter = items.GetItemText(0)[..Math.Min(3, items.GetItemText(0).Length)].ToLowerInvariant();
            search.Text = filter;
            InvokeSmoke(canvas, "RefreshCardLibrary");
            if (Enumerable.Range(0, items.ItemCount).All(index => items.GetItemMetadata(index).AsString() != firstChoice))
                throw new InvalidOperationException("Case-insensitive title search removed its matching card.");

            bool categoryReturnedCards = false;
            for (int index = 1; index < categoryPicker.ItemCount; index++)
            {
                categoryPicker.Select(index);
                InvokeSmoke(canvas, "RefreshCardLibrary");
                if (items.ItemCount > 0)
                {
                    categoryReturnedCards = true;
                    break;
                }
            }
            if (!categoryReturnedCards)
                throw new InvalidOperationException("No card category returned results.");

            categoryPicker.Select(0);
            search.Text = "";
            InvokeSmoke(canvas, "RefreshCardLibrary");
            await WaitForUiFrames(2);

            var hiddenMetadata = (Dictionary<string, JsonObject?>)typeof(LegacyPanelCanvasWindow)
                .GetField("_builtinMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(canvas)!;
            MethodInfo isHiddenMethod = typeof(LegacyPanelCanvasWindow)
                .GetMethod("IsHiddenMetadata", BindingFlags.Static | BindingFlags.NonPublic)!;
            var choices = Enumerable.Range(0, items.ItemCount)
                .Select(index => items.GetItemMetadata(index).AsString())
                .ToHashSet(StringComparer.Ordinal);
            int hiddenDefinitions = 0;
            foreach ((string uri, JsonObject? metadata) in hiddenMetadata)
            {
                JsonObject? cardData = metadata?["cardSystemCardData"] as JsonObject;
                if (cardData is null || !(bool)isHiddenMethod.Invoke(null, [cardData])!)
                    continue;
                hiddenDefinitions++;
                if (choices.Contains($"behavior:{uri}"))
                    throw new InvalidOperationException($"Hidden card '{uri}' appeared as a new library choice.");
            }

            var dropTargets = Descendants(canvas)
                .OfType<Button>()
                .Where(button => button.Name.ToString().StartsWith("CardLibraryDrop_", StringComparison.Ordinal))
                .ToArray();
            if (dropTargets.Length == 0)
                throw new InvalidOperationException("The sample canvas rendered no deck drop targets.");

            Button? acceptedTarget = null;
            Variant acceptedData = default;
            for (int index = 0; index < items.ItemCount && acceptedTarget is null; index++)
            {
                items.Select(index);
                items.EnsureCurrentIsVisible();
                await WaitForUiFrames(1);
                Rect2 itemRect = items.GetItemRect(index);
                Variant data = (Variant)InvokeSmoke(items, "_GetDragData", itemRect.GetCenter())!;
                if (data.VariantType != Variant.Type.String)
                    continue;
                foreach (Button target in dropTargets)
                {
                    if (!(bool)InvokeSmoke(target, "_CanDropData", Vector2.Zero, data)!)
                        continue;
                    acceptedTarget = target;
                    acceptedData = data;
                    break;
                }
            }
            if (acceptedTarget is null)
                throw new InvalidOperationException("No visible library card was accepted by a compatible deck.");

            JsonObject brain = (JsonObject)InvokeSmoke(canvas, "EnsureWritableBrain")!;
            JsonArray uses = (JsonArray)brain["behaviorUses"]!;
            HashSet<string> useIdsBefore = uses.OfType<JsonObject>()
                .Select(use => LegacySaveDocument.ReadString(use["id"]))
                .ToHashSet(StringComparer.Ordinal);
            string targetName = acceptedTarget.Name;
            InvokeSmoke(acceptedTarget, "_DropData", Vector2.Zero, acceptedData);
            brain = (JsonObject)InvokeSmoke(canvas, "EnsureWritableBrain")!;
            uses = (JsonArray)brain["behaviorUses"]!;
            (string panelUseId, string propertyName) = ParseDeckTargetName(targetName);
            JsonObject panelUse = FindUseSmoke(uses, panelUseId)
                ?? throw new InvalidOperationException("The deck target panel disappeared after the drop.");
            JsonArray assignedIds = (JsonArray)InvokeSmoke(canvas, "ReadAssignedValue", panelUse, propertyName)!;
            string assignedUseId = assignedIds.Select(node => LegacySaveDocument.ReadString(node))
                .FirstOrDefault(id => id.Length > 0 &&
                    (acceptedData.AsString().StartsWith("use:", StringComparison.Ordinal)
                        ? id == acceptedData.AsString()["use:".Length..]
                        : !useIdsBefore.Contains(id)
                            && LegacySaveDocument.ReadString(FindUseSmoke(uses, id)?["behaviorUri"]) == acceptedData.AsString()["behavior:".Length..]))
                ?? throw new InvalidOperationException("Dropping the library card did not persist its use ID in the .voos deck assignment.");

            int moveCount = 0;
            if (acceptedData.AsString().StartsWith("behavior:", StringComparison.Ordinal))
            {
                string useChoice = $"use:{assignedUseId}";
                foreach (Button target in Descendants(canvas).OfType<Button>()
                    .Where(button => button.Name.ToString().StartsWith("CardLibraryDrop_", StringComparison.Ordinal)))
                {
                    if (!(bool)InvokeSmoke(target, "_CanDropData", Vector2.Zero, Variant.From(useChoice))!)
                        continue;
                    string newTargetName = target.Name;
                    InvokeSmoke(target, "_DropData", Vector2.Zero, Variant.From(useChoice));
                    brain = (JsonObject)InvokeSmoke(canvas, "EnsureWritableBrain")!;
                    uses = (JsonArray)brain["behaviorUses"]!;
                    (string newPanelId, string newPropertyName) = ParseDeckTargetName(newTargetName);
                    JsonObject newPanel = FindUseSmoke(uses, newPanelId)
                        ?? throw new InvalidOperationException("The use-move destination panel disappeared.");
                    JsonArray movedIds = (JsonArray)InvokeSmoke(canvas, "ReadAssignedValue", newPanel, newPropertyName)!;
                    if (!movedIds.Any(node => LegacySaveDocument.ReadString(node) == assignedUseId)
                        || FindUseSmoke(uses, assignedUseId) is null)
                        throw new InvalidOperationException("Moving an assigned use changed or lost its .voos use ID.");
                    moveCount++;
                    break;
                }
            }

            await RunCardManagerSmoke(canvas);
            await WaitForUiFrames(3);
            string screenshotPath = ProjectSettings.GlobalizePath("res://.godot/card-library-smoke.png");
            Error screenshotError = GetViewport().GetTexture().GetImage().SavePng(screenshotPath);
            if (screenshotError != Error.Ok)
                throw new InvalidOperationException($"Could not save the card library screenshot: {screenshotError}.");
            await RunCustomAssetImportSmoke();
            GD.Print($"CARD_LIBRARY_SMOKE_OK cards={choices.Count} hiddenDefinitions={hiddenDefinitions} drop={acceptedData.AsString()} movedUse={moveCount} screenshot={screenshotPath}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"CARD_LIBRARY_SMOKE_FAILED: {exception}");
        }
        GetTree().Quit(exitCode);
    }
    private async void RunAssetImportSmoke()
    {
        int exitCode = 0;
        try
        {
            await RunCustomAssetImportSmoke();
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ASSET_IMPORT_SMOKE_FAILED: {exception}");
        }
        finally
        {
            GetTree().Quit(exitCode);
        }
    }

    private async System.Threading.Tasks.Task RunCustomAssetImportSmoke()
    {
        string originalProjectPath = OS.GetCmdlineUserArgs()
            .First(argument => argument.EndsWith(".voos", StringComparison.OrdinalIgnoreCase));
        string temporaryDirectory = Path.Combine(OS.GetUserDataDir(), $"asset-import-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string projectPath = Path.Combine(temporaryDirectory, "asset-smoke.voos");
        try
        {
            _document!.SaveTo(projectPath);
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            if (_document?.SourcePath != projectPath)
                throw new InvalidOperationException("The writable asset-import smoke project did not load.");

            OpenAssetStore();
            await WaitForUiFrames(2);
            Button storeImport = (Button)_assetStoreWindow.FindChild("AssetStoreImportModel", true, false);
            bool useNativeDialog = _importModelDialog.UseNativeDialog;
            _importModelDialog.UseNativeDialog = false;
            storeImport.EmitSignal("pressed");
            await WaitForUiFrames(2);
            if (!_importModelDialog.Visible)
                throw new InvalidOperationException("The store's local-import action did not open the model chooser.");
            _importModelDialog.Hide();
            _importModelDialog.UseNativeDialog = useNativeDialog;

            string sourceModelPath = Path.Combine(temporaryDirectory, "smoke-model.glb");
            var sourceRoot = new Node3D { Name = "ImportedSmokeModel" };
            sourceRoot.AddChild(new MeshInstance3D
            {
                Name = "SmokeBox",
                Mesh = new BoxMesh { Size = new Vector3(1f, 2f, 3f) }
            });
            var exportDocument = new GltfDocument();
            var exportState = new GltfState();
            Error appendResult = exportDocument.AppendFromScene(sourceRoot, exportState);
            sourceRoot.Free();
            if (appendResult != Error.Ok)
                throw new InvalidOperationException($"Could not generate the GLB smoke fixture: {appendResult}.");
            Error writeResult = exportDocument.WriteToFilesystem(exportState, sourceModelPath);
            if (writeResult != Error.Ok)
                throw new InvalidOperationException($"Could not write the GLB smoke fixture: {writeResult}.");

            ImportModelFromPath(sourceModelPath);
            if (_assetPicker.Selected < 0)
                throw new InvalidOperationException("The imported model was not selected in the asset browser.");
            string customUri = _assetPicker.GetItemMetadata(_assetPicker.Selected).AsString();
            string gltfUri = "";
            string fbxUri = "";
            if (!customUri.StartsWith("custom:", StringComparison.Ordinal)
                || !_catalog.TryGet(customUri, out RenderableEntry importedEntry)
                || !_assetPreview.Has3DPreview)
                throw new InvalidOperationException("The imported GLB was not catalogued and rendered in the 3D preview.");
            if (!File.Exists(importedEntry.ModelPaths[0]))
                throw new InvalidOperationException("The imported GLB was not copied into the project sidecar.");

            OpenAssetStore();
            await WaitForUiFrames(2);
            LineEdit storeSearch = (LineEdit)_assetStoreWindow.FindChild("AssetStoreSearch", true, false);
            ItemList storeItems = (ItemList)_assetStoreWindow.FindChild("AssetStoreItems", true, false);
            storeSearch.Text = "smoke-model";
            _assetStoreWindow.RefreshCatalog();
            int[] storeSelection = storeItems.GetSelectedItems();
            Label storeLicense = (Label)_assetStoreWindow.FindChild("AssetStoreLicenseDetails", true, false);
            var storePreview = (LegacyAssetPreviewControl)_assetStoreWindow.FindChild("LegacyAssetPreview", true, false);
            if (storeItems.ItemCount != 1
                || storeSelection.Length != 1
                || storeItems.GetItemMetadata(storeSelection[0]).AsString() != customUri
                || !storePreview.Has3DPreview
                || !storeLicense.Text.Contains("does not verify its license", StringComparison.Ordinal))
                throw new InvalidOperationException("The imported model or its unverified-license warning did not appear in the store.");
            _assetStoreWindow.Hide();
            AddActor();
            await WaitForUiFrames(2);
            if (!HasImportedActor(customUri))
                throw new InvalidOperationException("Adding the imported asset did not create a rendered actor.");
            string sourceGltfPath = Path.Combine(temporaryDirectory, "smoke-model.gltf");
            var gltfRoot = new Node3D { Name = "ImportedGltfSmokeModel" };
            gltfRoot.AddChild(new MeshInstance3D
            {
                Name = "SmokeSphere",
                Mesh = new SphereMesh { Radius = 0.75f, Height = 1.5f }
            });
            var gltfExportDocument = new GltfDocument();
            var gltfExportState = new GltfState();
            Error gltfAppendResult = gltfExportDocument.AppendFromScene(gltfRoot, gltfExportState);
            gltfRoot.Free();
            if (gltfAppendResult != Error.Ok)
                throw new InvalidOperationException($"Could not generate the glTF smoke fixture: {gltfAppendResult}.");
            Error gltfWriteResult = gltfExportDocument.WriteToFilesystem(gltfExportState, sourceGltfPath);
            if (gltfWriteResult != Error.Ok)
                throw new InvalidOperationException($"Could not write the glTF smoke fixture: {gltfWriteResult}.");

            ImportModelFromPath(sourceGltfPath);
            if (_assetPicker.Selected < 0)
                throw new InvalidOperationException("The imported glTF was not selected in the asset browser.");
            gltfUri = _assetPicker.GetItemMetadata(_assetPicker.Selected).AsString();
            if (gltfUri == customUri
                || !gltfUri.StartsWith("custom:", StringComparison.Ordinal)
                || !_catalog.TryGet(gltfUri, out RenderableEntry gltfEntry)
                || !Path.GetExtension(gltfEntry.ModelPaths[0]).Equals(".glb", StringComparison.OrdinalIgnoreCase)
                || !_assetPreview.Has3DPreview)
                throw new InvalidOperationException("The imported glTF was not bundled as a self-contained GLB and previewed.");
            AddActor();
            await WaitForUiFrames(2);
            if (!HasImportedActor(gltfUri))
                throw new InvalidOperationException("Adding the imported glTF did not create a rendered actor.");

            string fbxSourcePath = Path.GetFullPath(Path.Combine(
                ProjectSettings.GlobalizePath("res://"),
                "Assets",
                "GameAssets",
                "Models",
                "NPCs",
                "Fox",
                "FoxNPC_RunWalkLEFT.fbx"));
            if (!File.Exists(fbxSourcePath))
                throw new InvalidOperationException($"The animated FBX smoke fixture is missing: {fbxSourcePath}.");
            ImportModelFromPath(fbxSourcePath);
            if (_assetPicker.Selected < 0)
                throw new InvalidOperationException("The imported FBX was not selected in the asset browser.");
            fbxUri = _assetPicker.GetItemMetadata(_assetPicker.Selected).AsString();
            if (fbxUri == customUri
                || fbxUri == gltfUri
                || !fbxUri.StartsWith("custom:", StringComparison.Ordinal)
                || !_catalog.TryGet(fbxUri, out RenderableEntry fbxEntry)
                || !Path.GetExtension(fbxEntry.ModelPaths[0]).Equals(".glb", StringComparison.OrdinalIgnoreCase)
                || !_assetPreview.Has3DPreview)
                throw new InvalidOperationException($"The imported FBX was not bundled as a GLB and rendered in the 3D preview: {_errorDialog.DialogText}");

            Node3D? fbxInstance = LegacyRenderableCatalog.LoadModelInstance(fbxEntry.ModelPaths[0]);
            if (fbxInstance is null)
                throw new InvalidOperationException("The converted FBX could not be instantiated.");
            bool hasImportedAnimation = fbxInstance
                .FindChildren("*", nameof(AnimationPlayer), true, false)
                .OfType<AnimationPlayer>()
                .Any(player => player.GetAnimationList().Length > 0);
            fbxInstance.Free();
            if (!hasImportedAnimation)
                throw new InvalidOperationException("The converted FBX lost its animation clips.");
            AddActor();
            await WaitForUiFrames(2);
            if (!HasImportedActor(fbxUri))
                throw new InvalidOperationException("Adding the imported FBX did not create a rendered actor.");


            SaveTo(projectPath);
            if (!_status.Text.StartsWith("Saved ", StringComparison.Ordinal))
                throw new InvalidOperationException("Saving the project with imported models failed.");
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            if (!HasImportedActor(customUri) || !HasImportedActor(gltfUri) || !HasImportedActor(fbxUri))
                throw new InvalidOperationException("Imported model references did not survive a .voos reload.");

            string copyPath = Path.Combine(temporaryDirectory, "asset-smoke-copy.voos");
            SaveTo(copyPath);
            if (!_status.Text.StartsWith("Saved ", StringComparison.Ordinal)
                || !File.Exists(Path.Combine(temporaryDirectory, "asset-smoke-copy.assets", "manifest.json")))
                throw new InvalidOperationException("Save As did not copy the imported model sidecar.");
            LoadProject(copyPath);
            await WaitForUiFrames(2);
            if (!HasImportedActor(customUri) || !HasImportedActor(gltfUri) || !HasImportedActor(fbxUri))
                throw new InvalidOperationException("Imported model references did not survive Save As and reload.");

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/imported-asset-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the imported-asset browser screenshot.");
            GD.Print($"CUSTOM_ASSET_SMOKE_OK glb={customUri} gltf={gltfUri} animatedFbx={fbxUri} import=preview=animation=actor=.voos=save-as screenshot={screenshot}");
        }
        finally
        {
            LoadProject(originalProjectPath);
            await WaitForUiFrames(2);
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private bool HasImportedActor(string customUri)
    {
        WorldActorNode? actor = _actors.FirstOrDefault(candidate => candidate.RenderableUri == customUri);
        return actor is not null && actor.FindChildren("*", "MeshInstance3D", true, false).Count > 0;
    }

    private async System.Threading.Tasks.Task RunCardManagerSmoke(LegacyPanelCanvasWindow canvas)
    {
        Button manageButton = (Button)canvas.FindChild("ManageEmbeddedCards", true, false);
        manageButton.EmitSignal("pressed");
        await WaitForUiFrames(2);
        LegacyCardLibraryManagerWindow manager = canvas.GetChildren()
            .OfType<LegacyCardLibraryManagerWindow>()
            .Single();
        var conflictDialog = (ConfirmationDialog)typeof(LegacyCardLibraryManagerWindow)
            .GetField("_conflictDialog", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
        ItemList cards = (ItemList)manager.FindChild("ManagedCards", true, false);
        string managerScreenshot = ProjectSettings.GlobalizePath("res://.godot/card-manager-smoke.png");
        if (manager.GetViewport().GetTexture().GetImage().SavePng(managerScreenshot) != Error.Ok)
            throw new InvalidOperationException("Could not save the card manager screenshot.");

        JsonObject database = _document!.BehaviorDatabase
            ?? throw new InvalidOperationException("The card demo has no behavior database.");
        JsonArray ids = (JsonArray)database["behaviorIds"]!;
        int initialBehaviorCount = ids.Count;
        ((Button)manager.FindChild("CreateEmbeddedCard", true, false)).EmitSignal("pressed");
        if (ids.Count != initialBehaviorCount + 1)
            throw new InvalidOperationException("Creating a card did not add an embedded behavior.");
        string createdUri = cards.GetItemMetadata(cards.GetSelectedItems()[0]).AsString();
        if (!createdUri.StartsWith("embedded:", StringComparison.Ordinal))
            throw new InvalidOperationException("The new card was not selected as an embedded behavior.");
        string createdId = createdUri["embedded:".Length..];

        ((LineEdit)manager.FindChild("ManagedCardTitle", true, false)).Text = "Smoke Edited Card";
        ((LineEdit)manager.FindChild("ManagedCardCategories", true, false)).Text = "Custom, Smoke";
        ((LineEdit)manager.FindChild("ManagedCardImagePath", true, false)).Text = "BuiltinAssets/CardImages/placeholder";
        ((TextEdit)manager.FindChild("ManagedCardDescription", true, false)).Text = "Edited through the manager.";
        InvokeSmoke(manager, "ApplyMetadata");
        ((TextEdit)manager.FindChild("ManagedCardJavaScript", true, false)).Text =
            "export function onCollision(msg) { broadcast('SmokeEdited'); }";
        InvokeSmoke(manager, "ApplyCode");
        JsonObject createdBehavior = FindEmbeddedBehaviorSmoke(createdId)
            ?? throw new InvalidOperationException("The edited embedded card disappeared.");
        JsonObject createdMetadata = JsonNode.Parse(LegacySaveDocument.ReadString(createdBehavior["metadataJson"]))!.AsObject();
        JsonObject cardData = (JsonObject)createdMetadata["cardSystemCardData"]!;
        if (LegacySaveDocument.ReadString(cardData["title"]) != "Smoke Edited Card"
            || LegacySaveDocument.ReadString(createdBehavior["javascript"]) != "export function onCollision(msg) { broadcast('SmokeEdited'); }")
            throw new InvalidOperationException("Card metadata or JavaScript edits were not persisted in the .voos behavior record.");

        string builtinUri = Enumerable.Range(0, cards.ItemCount)
            .Select(index => cards.GetItemMetadata(index).AsString())
            .FirstOrDefault(uri => uri.StartsWith("builtin:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("No built-in card was available to copy.");
        SelectManagedCard(manager, builtinUri);
        int beforeCopy = ids.Count;
        InvokeSmoke(manager, "CopySelectedCard");
        if (ids.Count != beforeCopy + 1)
            throw new InvalidOperationException("Copying a built-in card did not create an embedded card.");
        string copiedUri = cards.GetItemMetadata(cards.GetSelectedItems()[0]).AsString();
        JsonObject copiedBehavior = FindEmbeddedBehaviorSmoke(copiedUri["embedded:".Length..])
            ?? throw new InvalidOperationException("The copied built-in card could not be found.");
        JsonObject copiedMetadata = JsonNode.Parse(LegacySaveDocument.ReadString(copiedBehavior["metadataJson"]))!.AsObject();
        if (!LegacySaveDocument.ReadString((copiedMetadata["cardSystemCardData"] as JsonObject)?["title"]).StartsWith("Copy of ", StringComparison.Ordinal))
            throw new InvalidOperationException("The built-in card copy did not retain editable card metadata.");

        string exportRoot = ProjectSettings.GlobalizePath("res://.godot/card-pack-smoke");
        if (Directory.Exists(exportRoot))
            Directory.Delete(exportRoot, recursive: true);
        Directory.CreateDirectory(exportRoot);
        try
        {
            SelectManagedCard(manager, createdUri);
            InvokeSmoke(manager, "ExportDirectorySelected", exportRoot);
            string packDirectory = Directory.GetDirectories(exportRoot).Single();
            string sourceFile = Directory.GetFiles(packDirectory, "*.js", SearchOption.AllDirectories).Single();
            string packScript = "export function onCollision(msg) { broadcast('PackOverwrite'); }";
            File.WriteAllText(sourceFile, packScript);
            InvokeSmoke(manager, "ImportDirectorySelected", packDirectory);
            InvokeSmoke(manager, "CompleteImport", true);
            conflictDialog.Hide();
            if (LegacySaveDocument.ReadString(FindEmbeddedBehaviorSmoke(createdId)?["javascript"]) != packScript)
                throw new InvalidOperationException("The overwrite conflict choice did not replace the existing card data.");

            int beforeDuplicate = ids.Count;
            InvokeSmoke(manager, "ImportDirectorySelected", packDirectory);
            InvokeSmoke(manager, "CompleteImport", false);
            conflictDialog.Hide();
            if (ids.Count != beforeDuplicate + 1)
                throw new InvalidOperationException("The duplicate conflict choice did not allocate a fresh embedded ID.");
            string duplicateUri = cards.GetItemMetadata(cards.GetSelectedItems()[0]).AsString();
            if (!duplicateUri.StartsWith("embedded:", StringComparison.Ordinal)
                || duplicateUri == createdUri
                || LegacySaveDocument.ReadString(FindEmbeddedBehaviorSmoke(duplicateUri["embedded:".Length..])?["javascript"]) != packScript)
                throw new InvalidOperationException("The duplicated pack card lost its content or retained the conflicting ID.");

            int beforeCancel = ids.Count;
            InvokeSmoke(manager, "ImportDirectorySelected", packDirectory);
            conflictDialog.EmitSignal("custom_action", new StringName("cancel"));
            conflictDialog.Hide();
            if (ids.Count != beforeCancel)
                throw new InvalidOperationException("Cancelling a card-pack conflict changed the project.");

            string freshPack = Path.Combine(exportRoot, "fresh-pack");
            Directory.CreateDirectory(freshPack);
            string modernId = Guid.NewGuid().ToString("N");
            string modernCard = Path.Combine(freshPack, "Modern card");
            Directory.CreateDirectory(modernCard);
            File.WriteAllText(Path.Combine(modernCard, $"{modernId}.js"), "export function onCollision(msg) { broadcast('ModernPack'); }");
            File.WriteAllText(Path.Combine(modernCard, $"{modernId}.json"), CreateSmokeCardMetadata("Modern imported card"));
            string legacyId = Guid.NewGuid().ToString("N");
            string legacyCard = Path.Combine(freshPack, legacyId);
            Directory.CreateDirectory(legacyCard);
            File.WriteAllText(Path.Combine(legacyCard, "source.js"), "export function onCollision(msg) { broadcast('LegacyPack'); }");
            File.WriteAllText(Path.Combine(legacyCard, "metadata.json"), CreateSmokeCardMetadata("Legacy imported card"));
            int beforeFreshImport = ids.Count;
            InvokeSmoke(manager, "ImportDirectorySelected", freshPack);
            if (ids.Count != beforeFreshImport + 2
                || FindEmbeddedBehaviorSmoke(modernId) is null
                || FindEmbeddedBehaviorSmoke(legacyId) is null)
                throw new InvalidOperationException("Modern and legacy card-pack folders did not import their cards.");

            JsonArray brains = (JsonArray)database["brains"]!;
            JsonObject targetBrain = brains.OfType<JsonObject>()
                .FirstOrDefault(brain => brain["behaviorUses"] is JsonArray)
                ?? throw new InvalidOperationException("The sample has no behavior brain for the delete-reference check.");
            JsonArray brainUses = (JsonArray)targetBrain["behaviorUses"]!;
            brainUses.Add(new JsonObject { ["id"] = "smoke-card-use", ["behaviorUri"] = createdUri });
            SelectManagedCard(manager, createdUri);
            InvokeSmoke(manager, "ConfirmDeleteSelectedCard");
            if (FindEmbeddedBehaviorSmoke(createdId) is null)
                throw new InvalidOperationException("A card referenced by a behavior brain was deleted.");
            brainUses.RemoveAt(brainUses.Count - 1);

            SelectManagedCard(manager, duplicateUri);
            InvokeSmoke(manager, "DeleteSelectedCard");
            if (FindEmbeddedBehaviorSmoke(duplicateUri["embedded:".Length..]) is not null)
                throw new InvalidOperationException("An unused embedded card could not be deleted.");
            SelectManagedCard(manager, copiedUri);
            InvokeSmoke(manager, "DeleteSelectedCard");
            if (FindEmbeddedBehaviorSmoke(copiedUri["embedded:".Length..]) is not null)
                throw new InvalidOperationException("An unused built-in copy could not be deleted.");
        }
        finally
        {
            if (Directory.Exists(exportRoot))
                Directory.Delete(exportRoot, recursive: true);
        }
        manager.QueueFree();
        await WaitForUiFrames(1);
        GD.Print($"CARD_MANAGER_SMOKE_OK embeddedCreated={createdId} copy={copiedUri} importOverwrite=duplicate=cancel=modern+legacy deleteReference=protected screenshot={managerScreenshot}");
    }

    private static void SelectManagedCard(LegacyCardLibraryManagerWindow manager, string uri)
    {
        ItemList cards = (ItemList)manager.FindChild("ManagedCards", true, false);
        for (int index = 0; index < cards.ItemCount; index++)
        {
            if (cards.GetItemMetadata(index).AsString() != uri)
                continue;
            cards.Select(index);
            InvokeSmoke(manager, "OnCardSelected", (long)index);
            return;
        }
        throw new InvalidOperationException($"Managed card '{uri}' was not listed.");
    }

    private JsonObject? FindEmbeddedBehaviorSmoke(string id)
    {
        if (_document?.BehaviorDatabase is not JsonObject database
            || database["behaviorIds"] is not JsonArray ids
            || database["behaviors"] is not JsonArray behaviors)
            return null;
        int index = FindStringIndexSmoke(ids, id);
        return index >= 0 && index < behaviors.Count ? behaviors[index] as JsonObject : null;
    }

    private static int FindStringIndexSmoke(JsonArray values, string expected)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (LegacySaveDocument.ReadString(values[index]) == expected)
                return index;
        }
        return -1;
    }

    private static string CreateSmokeCardMetadata(string title)
        => new JsonObject
        {
            ["cardSystemCardData"] = new JsonObject
            {
                ["isCard"] = true,
                ["title"] = title,
                ["categories"] = new JsonArray { "Custom" },
                ["description"] = "Pack import verification.",
                ["imageResourcePath"] = ""
            }
        }.ToJsonString();

    private WorldActorNode? FindActorWithCardDeck()
    {
        if (_document?.BehaviorDatabase is not JsonObject database
            || database["brainIds"] is not JsonArray brainIds
            || database["brains"] is not JsonArray brains)
            return _actors.FirstOrDefault();

        foreach (WorldActorNode actor in _actors)
        {
            string brainId = actor.State.BrainName;
            int brainIndex = -1;
            for (int index = 0; index < brainIds.Count; index++)
            {
                if (LegacySaveDocument.ReadString(brainIds[index]) == brainId)
                {
                    brainIndex = index;
                    break;
                }
            }
            if (brainIndex < 0 || brainIndex >= brains.Count || brains[brainIndex] is not JsonObject brain
                || brain["behaviorUses"] is not JsonArray uses)
                continue;
            if (uses.OfType<JsonObject>().Any(use =>
            {
                JsonArray? definitions = _behaviorRuntime?.GetBehaviorPropertyDefinitions(LegacySaveDocument.ReadString(use["behaviorUri"]));
                return definitions?.OfType<JsonObject>().Any(definition => LegacySaveDocument.ReadString(definition["type"]) == "CardDeck") == true;
            }))
                return actor;
        }
        return _actors.FirstOrDefault();
    }

    private async System.Threading.Tasks.Task WaitForUiFrames(int frames)
    {
        for (int index = 0; index < frames; index++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static object? InvokeSmoke(object target, string methodName, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        return method.Invoke(target, arguments);
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static (string PanelUseId, string PropertyName) ParseDeckTargetName(string name)
    {
        const string prefix = "CardLibraryDrop_";
        string value = name[prefix.Length..];
        int separator = value.IndexOf('_');
        if (separator <= 0 || separator == value.Length - 1)
            throw new InvalidOperationException($"Unexpected deck target name '{name}'.");
        return (value[..separator], value[(separator + 1)..]);
    }

    private static JsonObject? FindUseSmoke(JsonArray uses, string id)
        => uses.OfType<JsonObject>().FirstOrDefault(use => LegacySaveDocument.ReadString(use["id"]) == id);
}
