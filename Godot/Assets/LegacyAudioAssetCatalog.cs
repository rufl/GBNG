using System.Security.Cryptography;
using System.Text.Json;
using GBNG.Domain;
using System.Text.Json.Nodes;
using Godot;

namespace GBNG.Assets;

public sealed record LegacyAudioAsset(string Id, string Extension, string Path);

public static class LegacyAudioAssetCatalog
{
    private const string ManifestFileName = "manifest.json";
    private const string AudioDirectoryName = "sounds";

    public static LegacyAudioAsset ImportProjectAudio(string projectPath, string sourcePath)
    {
        if (projectPath.StartsWith("res://", StringComparison.Ordinal))
            throw new InvalidOperationException("Save a writable .voos copy before importing audio.");

        string source = Path.GetFullPath(sourcePath);
        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (!File.Exists(source) || !IsSupportedExtension(extension))
            throw new InvalidDataException("Choose an existing WAV (.wav) or Ogg Vorbis (.ogg) audio file.");
        if (LoadAudioStream(source, extension) is null)
            throw new InvalidDataException("Godot could not decode this audio file or it contains no samples.");

        string id = Guid.NewGuid().ToString("N");
        string sidecarDirectory = GetProjectAssetDirectory(projectPath);
        string audioDirectory = Path.Combine(sidecarDirectory, AudioDirectoryName);
        Directory.CreateDirectory(audioDirectory);
        string destination = Path.Combine(audioDirectory, $"{id}{extension}");
        try
        {
            EnsureManifest(Path.Combine(sidecarDirectory, ManifestFileName));
            File.Copy(source, destination);
        }
        catch
        {
            if (File.Exists(destination))
                File.Delete(destination);
            throw;
        }
        return new LegacyAudioAsset(id, extension[1..], destination);
    }

    public static AudioStream? LoadStream(string projectPath, JsonObject content)
    {
        if (!TryGetAssetPath(projectPath, content, out string path, out string extension))
            return null;
        try
        {
            return LoadAudioStream(path, extension);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException)
        {
            return null;
        }
    }

    internal static bool HasAudioAssets(string sidecarDirectory)
    {
        string audioDirectory = Path.Combine(sidecarDirectory, AudioDirectoryName);
        return Directory.Exists(audioDirectory) && Directory.EnumerateFileSystemEntries(audioDirectory).Any();
    }

    internal static void CopyProjectAssets(string sourceSidecarDirectory, string destinationSidecarDirectory)
    {
        string sourceDirectory = Path.Combine(sourceSidecarDirectory, AudioDirectoryName);
        if (!Directory.Exists(sourceDirectory))
            return;

        string destinationDirectory = Path.Combine(destinationSidecarDirectory, AudioDirectoryName);
        foreach (string sourcePath in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            string fileName = Path.GetFileName(sourcePath);
            if (!File.Exists(sourcePath) || !IsValidAudioFileName(fileName))
                throw new InvalidDataException($"Invalid local audio asset file: {sourcePath}");
            Directory.CreateDirectory(destinationDirectory);
            string destinationPath = Path.Combine(destinationDirectory, fileName);
            if (File.Exists(destinationPath))
            {
                if (!FilesEqual(sourcePath, destinationPath))
                    throw new IOException($"The destination audio asset already exists with different contents: {destinationPath}");
                continue;
            }
            File.Copy(sourcePath, destinationPath);
        }
    }

    private static bool TryGetAssetPath(string projectPath, JsonObject content, out string path, out string extension)
    {
        path = string.Empty;
        extension = string.Empty;
        if (projectPath.StartsWith("res://", StringComparison.Ordinal))
            return false;

        string id = LegacySaveDocument.ReadString(content["localAssetId"]);
        string storedExtension = LegacySaveDocument.ReadString(content["localAssetExtension"]);
        extension = storedExtension.StartsWith(".", StringComparison.Ordinal) ? storedExtension : $".{storedExtension}";
        if (!Guid.TryParseExact(id, "N", out _) || !IsSupportedExtension(extension))
            return false;

        path = Path.Combine(GetProjectAssetDirectory(projectPath), AudioDirectoryName, $"{id}{extension.ToLowerInvariant()}");
        return File.Exists(path);
    }

    private static AudioStream? LoadAudioStream(string path, string extension)
    {
        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            AudioStreamWav? wav = AudioStreamWav.LoadFromFile(path);
            return wav is { Data.Length: > 0 } ? wav : null;
        }
        if (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            AudioStreamOggVorbis? ogg = AudioStreamOggVorbis.LoadFromFile(path);
            return ogg is not null && ogg.GetLength() > 0d ? ogg : null;
        }
        return null;
    }

    private static void EnsureManifest(string manifestPath)
    {
        if (File.Exists(manifestPath))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("formatVersion", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != 1
                || !root.TryGetProperty("assets", out JsonElement assets)
                || assets.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Unsupported imported-asset manifest: {manifestPath}");
            return;
        }

        string temporaryPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new { formatVersion = 1, assets = Array.Empty<object>() }));
            File.Move(temporaryPath, manifestPath);
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

    private static bool IsSupportedExtension(string extension)
        => extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidAudioFileName(string fileName)
        => Path.GetFileName(fileName) == fileName
            && IsSupportedExtension(Path.GetExtension(fileName))
            && Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "N", out _);

    private static string GetProjectAssetDirectory(string projectPath)
    {
        string fullPath = Path.GetFullPath(projectPath);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"The project has no parent directory: {projectPath}");
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fullPath)}.assets");
    }
}
