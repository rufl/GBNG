using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace GBNG.Assets;

public static class LegacyBehaviorImages
{
    private const string ResourcesRoot = "res://Assets/Resources/";
    private const string PlaceholderIcon = "BuiltinAssets/CardImages/placeholder";
    private static readonly Dictionary<string, Texture2D> TextureCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string[]> DirectoryFiles = new(StringComparer.Ordinal);

    public static Texture2D? LoadCardIcon(string imageResourcePath)
    {
        if (string.IsNullOrWhiteSpace(imageResourcePath))
            return null;

        string normalized = imageResourcePath.Trim().Replace('\\', '/');
        Texture2D? texture = LoadExact(normalized);
        return texture ?? (normalized == PlaceholderIcon ? null : LoadExact(PlaceholderIcon));
    }

    private static Texture2D? LoadExact(string legacyResourcePath)
    {
        if (legacyResourcePath.StartsWith("/", StringComparison.Ordinal)
            || legacyResourcePath == ".."
            || legacyResourcePath.StartsWith("../", StringComparison.Ordinal)
            || legacyResourcePath.Contains("/../", StringComparison.Ordinal)
            || legacyResourcePath.EndsWith("/..", StringComparison.Ordinal))
            return null;

        if (TextureCache.TryGetValue(legacyResourcePath, out Texture2D? cached))
            return cached;

        string resourcePath = legacyResourcePath.StartsWith("res://", StringComparison.Ordinal)
            ? legacyResourcePath
            : legacyResourcePath.StartsWith("Assets/Resources/", StringComparison.Ordinal)
                ? $"res://{legacyResourcePath}"
                : $"{ResourcesRoot}{legacyResourcePath}";
        if (!Path.HasExtension(resourcePath))
            resourcePath += ".png";
        string? resolvedPath = ResolveCaseCorrectFilePath(resourcePath);
        if (resolvedPath is null)
            return null;

        Texture2D? texture = ResourceLoader.Load<Texture2D>(resolvedPath);
        if (texture is not null)
            TextureCache.Add(legacyResourcePath, texture);
        return texture;
    }

    private static string? ResolveCaseCorrectFilePath(string resourcePath)
    {
        int lastSlash = resourcePath.LastIndexOf('/');
        if (lastSlash < 0)
            return null;
        string directory = resourcePath[..lastSlash];
        string fileName = resourcePath[(lastSlash + 1)..];
        if (!DirectoryFiles.TryGetValue(directory, out string[]? files))
        {
            files = DirAccess.GetFilesAt(directory);
            DirectoryFiles.Add(directory, files);
        }
        foreach (string file in files)
        {
            if (file.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return $"{directory}/{file}";
        }
        return null;
    }
}
