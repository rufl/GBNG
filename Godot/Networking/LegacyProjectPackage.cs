using Godot;
using System.IO.Compression;

namespace GBNG.Networking;

internal static class LegacyProjectPackage
{
    internal const int MaximumPackageBytes = 256 * 1024 * 1024;
    private const long MaximumExpandedBytes = 256L * 1024 * 1024;
    private const int MaximumEntries = 10_000;
    private const string ProjectDirectory = "project/";

    public static byte[] Create(string sourcePath)
    {
        string fullProjectPath = ResolvePath(sourcePath);
        if (!File.Exists(fullProjectPath) || !string.Equals(Path.GetExtension(fullProjectPath), ".voos", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The host project must be an existing .voos file.");

        string projectName = Path.GetFileName(fullProjectPath);
        string assetDirectory = Path.Combine(
            Path.GetDirectoryName(fullProjectPath) ?? throw new InvalidDataException("The project has no parent directory."),
            $"{Path.GetFileNameWithoutExtension(fullProjectPath)}.assets");
        var files = new List<(string FullPath, string ArchivePath)> { (fullProjectPath, ProjectDirectory + projectName) };
        if (Directory.Exists(assetDirectory))
            AddAssetFiles(assetDirectory, Path.GetFileName(assetDirectory), files);
        if (files.Count > MaximumEntries)
            throw new InvalidDataException($"Project handoff exceeds the {MaximumEntries:N0}-file limit.");

        long uncompressedBytes = 0;
        foreach ((string filePath, _) in files)
        {
            uncompressedBytes = checked(uncompressedBytes + new FileInfo(filePath).Length);
            if (uncompressedBytes > MaximumExpandedBytes)
                throw new InvalidDataException("Project handoff exceeds the 256 MiB expanded-size limit.");
        }

        using var package = new MemoryStream();
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string filePath, string archivePath) in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(archivePath, CompressionLevel.Fastest);
                using Stream destination = entry.Open();
                using FileStream source = new(filePath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
                source.CopyTo(destination);
                if (package.Length > MaximumPackageBytes)
                    throw new InvalidDataException("Project handoff exceeds the 256 MiB transfer limit.");
            }
        }

        if (package.Length > MaximumPackageBytes)
            throw new InvalidDataException("Project handoff exceeds the 256 MiB transfer limit.");
        return package.ToArray();
    }

    public static string Extract(byte[] packageBytes)
    {
        if (packageBytes.Length == 0 || packageBytes.Length > MaximumPackageBytes)
            throw new InvalidDataException("The received project package has an invalid size.");

        string destinationRoot = ProjectSettings.GlobalizePath($"user://network-projects/{Guid.NewGuid():N}");
        Directory.CreateDirectory(destinationRoot);
        try
        {
            using var package = new MemoryStream(packageBytes, writable: false);
            using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count is 0 or > MaximumEntries)
                throw new InvalidDataException("The received project package has an invalid file count.");

            long expandedBytes = 0;
            string? projectRelativePath = null;
            string? projectBaseName = null;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string relativePath = ValidateArchivePath(entry.FullName);
                if (relativePath.Length == 0)
                    continue;

                if (relativePath.StartsWith(ProjectDirectory, StringComparison.Ordinal))
                {
                    string projectRelative = relativePath[ProjectDirectory.Length..];
                    if (!projectRelative.Contains('/') && string.Equals(Path.GetExtension(projectRelative), ".voos", StringComparison.OrdinalIgnoreCase))
                    {
                        if (projectRelativePath is not null)
                            throw new InvalidDataException("The received package contains multiple .voos projects.");
                        projectRelativePath = relativePath;
                        projectBaseName = Path.GetFileNameWithoutExtension(projectRelative);
                    }
                }

                if (entry.Length < 0 || entry.Length > MaximumExpandedBytes)
                    throw new InvalidDataException("The received project package contains an invalid file size.");
                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > MaximumExpandedBytes)
                    throw new InvalidDataException("The received project expands beyond the 256 MiB limit.");

                string destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                string rootPrefix = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
                if (!destinationPath.StartsWith(rootPrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("The received project package contains an unsafe path.");
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                using Stream source = entry.Open();
                using FileStream destination = new(destinationPath, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None);
                CopyBounded(source, destination, entry.Length);
            }

            if (projectRelativePath is null || projectBaseName is null)
                throw new InvalidDataException("The received package does not contain a .voos project.");
            string sidecarPath = Path.Combine(destinationRoot, ProjectDirectory, $"{projectBaseName}.assets");
            if (Directory.Exists(sidecarPath))
                Directory.Move(sidecarPath, Path.Combine(destinationRoot, $"{projectBaseName}.assets"));
            string extractedProjectPath = Path.Combine(destinationRoot, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));
            string projectPath = Path.Combine(destinationRoot, Path.GetFileName(projectRelativePath));
            File.Move(extractedProjectPath, projectPath);
            return projectPath;
        }
        catch
        {
            if (Directory.Exists(destinationRoot))
                Directory.Delete(destinationRoot, recursive: true);
            throw;
        }
    }

    private static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A host project path is required.", nameof(path));
        return Path.GetFullPath(path.StartsWith("res://", StringComparison.Ordinal)
            || path.StartsWith("user://", StringComparison.Ordinal)
            ? ProjectSettings.GlobalizePath(path)
            : path);
    }

    private static void AddAssetFiles(string root, string sidecarName, List<(string FullPath, string ArchivePath)> files)
    {
        string rootFullPath = Path.GetFullPath(root);
        var pending = new Stack<string>();
        pending.Push(rootFullPath);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entryPath in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                FileAttributes attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entryPath);
                    continue;
                }

                string relativePath = Path.GetRelativePath(rootFullPath, entryPath).Replace(Path.DirectorySeparatorChar, '/');
                files.Add((entryPath, ProjectDirectory + sidecarName + "/" + relativePath));
            }
        }
    }

    private static string ValidateArchivePath(string entryName)
    {
        if (string.IsNullOrEmpty(entryName) || entryName.Contains('\\') || entryName[0] == '/' || entryName.Contains(':'))
            throw new InvalidDataException("The received project package contains an unsafe path.");
        string normalized = entryName.TrimEnd('/');
        if (normalized.Length == 0)
            return string.Empty;
        string[] components = normalized.Split('/');
        if (components.Any(component => component.Length == 0 || component is "." or ".."))
            throw new InvalidDataException("The received project package contains an unsafe path.");
        if (components[0] != "project" || components.Length > 1 && !components[1].EndsWith(".voos", StringComparison.OrdinalIgnoreCase)
            && !components[1].EndsWith(".assets", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The received project package contains an unexpected file.");
        return normalized;
    }

    private static void CopyBounded(Stream source, Stream destination, long expectedLength)
    {
        byte[] buffer = new byte[81_920];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied = checked(copied + read);
            if (copied > expectedLength || copied > MaximumExpandedBytes)
                throw new InvalidDataException("A received project file expanded beyond its declared size.");
            destination.Write(buffer, 0, read);
        }
        if (copied != expectedLength)
            throw new InvalidDataException("A received project file did not match its declared size.");
    }
}
