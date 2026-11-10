#if UNITY_EDITOR
using System;
using System.IO;
using NUnit.Framework;

public class SaveLoadControllerReadTests
{
  [Test]
  public void ReadSaveGameRejectsFilesWithoutWorldData()
  {
    string path = Path.Combine(Path.GetTempPath(), "GameBuilderInvalidSave-" + Guid.NewGuid().ToString("N") + ".voos");
    try
    {
      File.WriteAllText(path, "{}");

      InvalidDataException exception = Assert.Throws<InvalidDataException>(() => SaveLoadController.ReadSaveGame(path));

      StringAssert.Contains("missing world data", exception.Message);
    }
    finally
    {
      if (File.Exists(path)) File.Delete(path);
    }
  }
}
#endif
