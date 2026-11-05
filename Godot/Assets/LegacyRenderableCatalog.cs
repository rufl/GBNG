using System.Security.Cryptography;
using System.Text.Json;
using Godot;

namespace GBNG.Assets;

public sealed class LegacyRenderableCatalog
{
    private const string ManifestFileName = "manifest.json";
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly Dictionary<string, RenderableEntry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _projectAssetUris = new(StringComparer.Ordinal);

    private LegacyRenderableCatalog() { }
    public IReadOnlyCollection<RenderableEntry> Entries => _entries.Values;

    public static LegacyRenderableCatalog Load(string path = "res://Godot/Generated/renderables.json")
    {
        string json = Godot.FileAccess.GetFileAsString(path);
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

    public void LoadProjectAssets(string projectPath)
    {
        foreach (string uri in _projectAssetUris)
            _entries.Remove(uri);
        _projectAssetUris.Clear();
        if (projectPath.StartsWith("res://", StringComparison.Ordinal))
            return;

        string directory = GetProjectAssetDirectory(projectPath);
        Dictionary<string, string> assets = ReadProjectAssetManifest(Path.Combine(directory, ManifestFileName));
        foreach ((string uri, string fileName) in assets)
        {
            string assetPath = Path.Combine(directory, fileName);
            if (!File.Exists(assetPath))
                throw new FileNotFoundException($"The imported asset for '{uri}' is missing.", assetPath);
            RenderableEntry entry = Path.GetExtension(fileName).Equals(".glb", StringComparison.OrdinalIgnoreCase)
                ? new RenderableEntry(uri, string.Empty, [assetPath], Array.Empty<string>())
                : new RenderableEntry(uri, string.Empty, Array.Empty<string>(), [assetPath]);
            _entries.Add(uri, entry);
            _projectAssetUris.Add(uri);
        }
    }

    public RenderableEntry ImportProjectModel(string projectPath, string sourcePath)
    {
        if (projectPath.StartsWith("res://", StringComparison.Ordinal))
            throw new InvalidOperationException("Save a writable .voos copy before importing a model.");
        string source = Path.GetFullPath(sourcePath);
        string extension = Path.GetExtension(source);
        if (!File.Exists(source)
            || (!extension.Equals(".glb", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Choose an existing glTF (.glb or .gltf) or FBX (.fbx) model.");

        string id = Guid.NewGuid().ToString("N");
        string uri = $"custom:{id}/{Path.GetFileNameWithoutExtension(source)}";
        string fileName = $"{id}.glb";
        string directory = GetProjectAssetDirectory(projectPath);
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, fileName);
        try
        {
            if (extension.Equals(".glb", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, destination);
            }
            else
            {
                string basePath = Path.GetDirectoryName(source) ?? string.Empty;
                if (extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    var document = new FbxDocument();
                    var state = new FbxState();
                    Error appendResult = document.AppendFromFile(source, state, 0, basePath);
                    if (appendResult != Error.Ok)
                        throw new InvalidDataException($"Godot could not read this FBX model ({appendResult}).");
                    Node? importedScene = document.GenerateScene(state);
                    if (importedScene is not Node3D modelRoot)
                    {
                        importedScene?.Free();
                        throw new InvalidDataException("Godot could not generate a scene from this FBX model.");
                    }
                    try
                    {
                        var exportDocument = new GltfDocument();
                        var exportState = new GltfState();
                        Error sceneAppendResult = exportDocument.AppendFromScene(modelRoot, exportState);
                        if (sceneAppendResult != Error.Ok)
                            throw new InvalidDataException($"Godot could not prepare this FBX model for GLB export ({sceneAppendResult}).");
                        Error writeResult = exportDocument.WriteToFilesystem(exportState, destination);
                        if (writeResult != Error.Ok)
                            throw new InvalidDataException($"Godot could not convert this FBX model to a self-contained GLB ({writeResult}).");
                    }
                    finally
                    {
                        modelRoot.Free();
                    }
                }
                else
                {
                    var document = new GltfDocument();
                    var state = new GltfState();
                    if (document.AppendFromFile(source, state, 0, basePath) != Error.Ok
                        || document.WriteToFilesystem(state, destination) != Error.Ok)
                        throw new InvalidDataException("Godot could not convert this glTF model to a self-contained GLB.");
                }
            }

            Node3D? validationInstance = LoadModelInstance(destination);
            if (validationInstance is null)
                throw new InvalidDataException("Godot could not parse this imported model.");
            validationInstance.Free();
        }
        catch
        {
            if (File.Exists(destination))
                File.Delete(destination);
            throw;
        }

        return RegisterProjectAsset(
            new RenderableEntry(uri, string.Empty, [destination], Array.Empty<string>()),
            Path.Combine(directory, ManifestFileName),
            destination);
    }
    public RenderableEntry ImportProjectImage(string projectPath, string sourcePath)
    {
        if (projectPath.StartsWith("res://", StringComparison.Ordinal))
            throw new InvalidOperationException("Save a writable .voos copy before importing an image.");
        string source = Path.GetFullPath(sourcePath);
        string extension = Path.GetExtension(source);
        if (!File.Exists(source) || !IsSupportedImageExtension(extension))
            throw new InvalidDataException("Choose an existing PNG, JPEG, or WebP image.");
        Image decoded = Image.LoadFromFile(source);
        if (decoded.IsEmpty())
            throw new InvalidDataException("Godot could not decode this image.");

        string id = Guid.NewGuid().ToString("N");
        string uri = $"custom:{id}/{Path.GetFileNameWithoutExtension(source)}";
        string fileName = $"{id}{extension.ToLowerInvariant()}";
        string directory = GetProjectAssetDirectory(projectPath);
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, fileName);
        try
        {
            File.Copy(source, destination);
        }
        catch
        {
            if (File.Exists(destination))
                File.Delete(destination);
            throw;
        }
        return RegisterProjectAsset(
            new RenderableEntry(uri, string.Empty, Array.Empty<string>(), [destination]),
            Path.Combine(directory, ManifestFileName),
            destination);
    }


    public void CopyProjectAssets(string sourceProjectPath, string destinationProjectPath)
    {
        if (sourceProjectPath.StartsWith("res://", StringComparison.Ordinal))
            return;
        string sourceProject = Path.GetFullPath(sourceProjectPath);
        string destinationProject = Path.GetFullPath(destinationProjectPath);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(sourceProject, destinationProject, pathComparison))
            return;
        string sourceDirectory = GetProjectAssetDirectory(sourceProject);
        Dictionary<string, string> sourceAssets = ReadProjectAssetManifest(Path.Combine(sourceDirectory, ManifestFileName));
        bool hasAudioAssets = LegacyAudioAssetCatalog.HasAudioAssets(sourceDirectory);
        if (sourceAssets.Count == 0 && !hasAudioAssets)
            return;

        string destinationDirectory = GetProjectAssetDirectory(destinationProject);
        Directory.CreateDirectory(destinationDirectory);
        string destinationManifestPath = Path.Combine(destinationDirectory, ManifestFileName);
        Dictionary<string, string> destinationAssets = ReadProjectAssetManifest(destinationManifestPath);
        foreach ((string uri, string fileName) in sourceAssets)
        {
            string source = Path.Combine(sourceDirectory, fileName);
            if (!File.Exists(source))
                throw new FileNotFoundException($"The imported asset for '{uri}' is missing.", source);
            if (destinationAssets.TryGetValue(uri, out string? existingFile)
                && !string.Equals(existingFile, fileName, StringComparison.Ordinal))
                throw new IOException($"The destination already contains a different imported asset for '{uri}'.");

            string destination = Path.Combine(destinationDirectory, fileName);
            if (File.Exists(destination))
            {
                if (!FilesEqual(source, destination))
                    throw new IOException($"The destination asset already exists with different contents: {destination}");
            }
            else
            {
                File.Copy(source, destination);
            }
            destinationAssets[uri] = fileName;
        }
        LegacyAudioAssetCatalog.CopyProjectAssets(sourceDirectory, destinationDirectory);
        WriteProjectAssetManifest(destinationManifestPath, destinationAssets);
    }

    public bool TryGet(string uri, out RenderableEntry entry) => _entries.TryGetValue(uri, out entry!);

    public static Node3D? LoadModelInstance(string modelPath)
    {
        try
        {
            string extension = Path.GetExtension(modelPath);
            if (!modelPath.StartsWith("res://", StringComparison.Ordinal)
                && (extension.Equals(".glb", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase)))
            {
                var document = new GltfDocument();
                var state = new GltfState();
                string basePath = Path.GetDirectoryName(modelPath) ?? string.Empty;
                if (document.AppendFromFile(modelPath, state, 0, basePath) != Error.Ok)
                    return null;
                Node? gltfRoot = document.GenerateScene(state);
                if (gltfRoot is Node3D modelRoot)
                {
                    EnableAnisotropicTextureFiltering(modelRoot);
                    return modelRoot;
                }
                gltfRoot?.Free();
                return null;
            }

            PackedScene? packedScene = ResourceLoader.Load<PackedScene>(modelPath);
            Node? instance = packedScene?.Instantiate();
            if (instance is Node3D model)
            {
                EnableAnisotropicTextureFiltering(model);
                return model;
            }
            instance?.Free();
            return null;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Could not load model '{modelPath}': {exception.Message}");
            return null;
        }
    }
    internal static void EnableAnisotropicTextureFiltering(Node3D modelRoot)
    {
        if (modelRoot is MeshInstance3D rootMesh)
            EnableAnisotropicTextureFiltering(rootMesh);

        foreach (Node node in modelRoot.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node is MeshInstance3D meshInstance)
                EnableAnisotropicTextureFiltering(meshInstance);
        }
    }

    private static void EnableAnisotropicTextureFiltering(MeshInstance3D meshInstance)
    {
        if (meshInstance.MaterialOverride is Material materialOverride)
        {
            if (materialOverride is BaseMaterial3D baseOverride)
                EnableAnisotropicTextureFiltering(baseOverride);
            return;
        }

        if (meshInstance.Mesh is not Mesh mesh)
            return;

        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            Material? material = meshInstance.GetSurfaceOverrideMaterial(surface)
                ?? mesh.SurfaceGetMaterial(surface);
            if (material is BaseMaterial3D baseMaterial)
                EnableAnisotropicTextureFiltering(baseMaterial);
        }
    }

    private static void EnableAnisotropicTextureFiltering(BaseMaterial3D material)
    {
        material.TextureFilter = material.TextureFilter switch
        {
            BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps
                => BaseMaterial3D.TextureFilterEnum.NearestWithMipmapsAnisotropic,
            BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps
                => BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            _ => material.TextureFilter
        };
    }


    private static string GetProjectAssetDirectory(string projectPath)
    {
        string fullPath = Path.GetFullPath(projectPath);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"The project has no parent directory: {projectPath}");
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fullPath)}.assets");
    }

    private static Dictionary<string, string> ReadProjectAssetManifest(string manifestPath)
    {
        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(manifestPath))
            return assets;

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("formatVersion", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || version.GetInt32() != 1
            || !root.TryGetProperty("assets", out JsonElement entries)
            || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Unsupported imported-asset manifest: {manifestPath}");

        foreach (JsonElement item in entries.EnumerateArray())
        {
            string uri = item.TryGetProperty("uri", out JsonElement uriValue) && uriValue.ValueKind == JsonValueKind.String
                ? uriValue.GetString() ?? string.Empty
                : string.Empty;
            string fileName = item.TryGetProperty("file", out JsonElement fileValue) && fileValue.ValueKind == JsonValueKind.String
                ? fileValue.GetString() ?? string.Empty
                : string.Empty;
            if (!IsValidCustomUri(uri) || !IsValidAssetFileName(fileName))
                throw new InvalidDataException($"Invalid imported-asset entry in {manifestPath}.");
            assets.Add(uri, fileName);
        }
        return assets;
    }

    private static bool IsValidCustomUri(string uri)
    {
        if (!uri.StartsWith("custom:", StringComparison.Ordinal))
            return false;
        string value = uri["custom:".Length..];
        int separator = value.IndexOf('/');
        return separator > 0
            && Guid.TryParseExact(value[..separator], "N", out _)
            && separator < value.Length - 1;
    }

    private static bool IsValidAssetFileName(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        return Path.GetFileName(fileName) == fileName
            && (extension.Equals(".glb", StringComparison.OrdinalIgnoreCase) || IsSupportedImageExtension(extension))
            && Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "N", out _);
    }

    private static bool IsSupportedImageExtension(string extension)
    {
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private RenderableEntry RegisterProjectAsset(RenderableEntry entry, string manifestPath, string destination)
    {
        _entries.Add(entry.Uri, entry);
        _projectAssetUris.Add(entry.Uri);
        try
        {
            WriteProjectAssetManifest(
                manifestPath,
                _projectAssetUris.ToDictionary(
                    assetUri => assetUri,
                    assetUri => GetManifestFileName(_entries[assetUri]),
                    StringComparer.Ordinal));
        }
        catch
        {
            _entries.Remove(entry.Uri);
            _projectAssetUris.Remove(entry.Uri);
            if (File.Exists(destination))
                File.Delete(destination);
            throw;
        }
        return entry;
    }

    private static string GetManifestFileName(RenderableEntry entry)
    {
        string? assetPath = entry.ModelPaths.FirstOrDefault() ?? entry.ImagePaths.FirstOrDefault();
        return assetPath is null
            ? throw new InvalidDataException($"Imported asset '{entry.Uri}' has no project file.")
            : Path.GetFileName(assetPath);
    }

    private static void WriteProjectAssetManifest(string manifestPath, IReadOnlyDictionary<string, string> assets)
    {
        string temporaryPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        string json = JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            assets = assets.OrderBy(asset => asset.Key, StringComparer.Ordinal)
                .Select(asset => new { uri = asset.Key, file = asset.Value })
        }, ManifestOptions);
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static bool FilesEqual(string leftPath, string rightPath)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (leftInfo.Length != rightInfo.Length)
            return false;
        using FileStream left = File.OpenRead(leftPath);
        using FileStream right = File.OpenRead(rightPath);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(left), SHA256.HashData(right));
    }

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

