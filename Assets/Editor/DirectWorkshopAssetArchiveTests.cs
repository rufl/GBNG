#if UNITY_EDITOR
using System;
using System.IO;
using NUnit.Framework;

public class DirectWorkshopAssetArchiveTests
{
  [Test]
  public void ArchiveRoundTripPreservesNestedAssetFiles()
  {
    string root = Path.Combine(Path.GetTempPath(), "GameBuilderDirectAsset-" + Guid.NewGuid().ToString("N"));
    string source = Path.Combine(root, "source");
    string destination = Path.Combine(root, "destination");
    try
    {
      Directory.CreateDirectory(Path.Combine(source, "nested"));
      File.WriteAllText(Path.Combine(source, "model.fbx"), "model");
      File.WriteAllText(Path.Combine(source, "nested", "texture.png"), "texture");

      byte[] archive;
      string error;
      Assert.IsTrue(DirectWorkshopAssetArchive.TryCreate(source, out archive, out error), error);
      Assert.IsTrue(DirectWorkshopAssetArchive.TryExtract(archive, destination, out error), error);

      Assert.AreEqual("model", File.ReadAllText(Path.Combine(destination, "model.fbx")));
      Assert.AreEqual("texture", File.ReadAllText(Path.Combine(destination, "nested", "texture.png")));
    }
    finally
    {
      if (Directory.Exists(root)) Directory.Delete(root, true);
    }
  }
}
#endif
