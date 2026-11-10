using System;
using System.IO;
using System.IO.Compression;
using System.Text;

public static class DirectWorkshopAssetArchive
{
  public const int MaxBytes = 64 * 1024 * 1024;
  const int MaxFiles = 4096;

  public static bool TryCreate(string sourceDirectory, out byte[] archive, out string error)
  {
    archive = null;
    error = null;
    if (!Directory.Exists(sourceDirectory))
    {
      error = "The requested asset is unavailable on the host.";
      return false;
    }

    try
    {
      string root = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
      string[] files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
      if (files.Length > MaxFiles) throw new InvalidDataException("The asset contains too many files.");
      using (MemoryStream raw = new MemoryStream())
      using (BinaryWriter writer = new BinaryWriter(raw, Encoding.UTF8, true))
      {
        writer.Write(files.Length);
        foreach (string file in files)
        {
          string fullPath = Path.GetFullPath(file);
          if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Asset file escapes its root directory.");
          byte[] contents = File.ReadAllBytes(fullPath);
          if (contents.Length > MaxBytes || raw.Length + contents.Length > MaxBytes) throw new InvalidDataException("The asset exceeds the 64 MB direct-host limit.");
          writer.Write(fullPath.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/'));
          writer.Write(contents.Length);
          writer.Write(contents);
        }
        archive = Util.GZip(raw.ToArray());
      }
      if (archive.Length > MaxBytes) throw new InvalidDataException("The compressed asset exceeds the 64 MB direct-host limit.");
      return true;
    }
    catch (Exception exception)
    {
      archive = null;
      error = exception.Message;
      return false;
    }
  }

  public static bool TryExtract(byte[] archive, string destinationDirectory, out string error)
  {
    error = null;
    if (archive == null || archive.Length == 0 || archive.Length > MaxBytes)
    {
      error = "The host sent an invalid asset archive.";
      return false;
    }

    string stagingDirectory = destinationDirectory + ".incoming-" + Guid.NewGuid().ToString("N");
    try
    {
      byte[] raw = ReadGzipLimited(archive);
      using (BinaryReader reader = new BinaryReader(new MemoryStream(raw, false), Encoding.UTF8))
      {
        int fileCount = reader.ReadInt32();
        if (fileCount < 0 || fileCount > MaxFiles) throw new InvalidDataException("Invalid asset file count.");
        Directory.CreateDirectory(stagingDirectory);
        for (int index = 0; index < fileCount; index++)
        {
          string relativePath = reader.ReadString();
          int length = reader.ReadInt32();
          if (length < 0 || length > MaxBytes || reader.BaseStream.Length - reader.BaseStream.Position < length) throw new InvalidDataException("Invalid asset file length.");
          string outputPath = GetSafeOutputPath(stagingDirectory, relativePath);
          Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
          File.WriteAllBytes(outputPath, reader.ReadBytes(length));
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Unexpected data in asset archive.");
      }
      if (Directory.Exists(destinationDirectory)) Directory.Delete(destinationDirectory, true);
      Directory.Move(stagingDirectory, destinationDirectory);
      return true;
    }
    catch (Exception exception)
    {
      if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
      error = exception.Message;
      return false;
    }
  }

  static byte[] ReadGzipLimited(byte[] archive)
  {
    using (MemoryStream input = new MemoryStream(archive, false))
    using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
    using (MemoryStream output = new MemoryStream())
    {
      byte[] buffer = new byte[8192];
      int count;
      while ((count = gzip.Read(buffer, 0, buffer.Length)) > 0)
      {
        if (output.Length + count > MaxBytes) throw new InvalidDataException("The extracted asset exceeds the 64 MB direct-host limit.");
        output.Write(buffer, 0, count);
      }
      return output.ToArray();
    }
  }

  static string GetSafeOutputPath(string root, string relativePath)
  {
    if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath)) throw new InvalidDataException("Invalid asset file path.");
    string outputPath = Path.GetFullPath(Path.Combine(root, relativePath));
    string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!outputPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Asset file path escapes its root directory.");
    return outputPath;
  }
}
