#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

public class MacOSV8ArchitectureTests
{
  static readonly MethodInfo GetArchitectures = typeof(GameBuilderMenu).GetMethod(
    "GetMacOSV8Architectures", BindingFlags.Static | BindingFlags.NonPublic);

  [Test]
  public void MachOArchitectureParserRecognizesThinAndUniversalV8Bundles()
  {
    string tempDirectory = Path.Combine(Path.GetTempPath(), "GameBuilderMacOSV8-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
      string thinPath = Path.Combine(tempDirectory, "thin");
      File.WriteAllBytes(thinPath, new byte[]
      {
        0xcf, 0xfa, 0xed, 0xfe,
        0x07, 0x00, 0x00, 0x01
      });
      Assert.AreEqual(1, GetArchitectureFlags(thinPath));

      string universalPath = Path.Combine(tempDirectory, "universal");
      File.WriteAllBytes(universalPath, new byte[]
      {
        0xca, 0xfe, 0xba, 0xbe, 0x00, 0x00, 0x00, 0x02,
        0x01, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x03,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x0c, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
      });
      Assert.AreEqual(3, GetArchitectureFlags(universalPath));

      string truncatedPath = Path.Combine(tempDirectory, "truncated");
      File.WriteAllBytes(truncatedPath, new byte[]
      {
        0xca, 0xfe, 0xba, 0xbe, 0x00, 0x00, 0x00, 0x01
      });
      Assert.AreEqual(0, GetArchitectureFlags(truncatedPath));
    }
    finally
    {
      Directory.Delete(tempDirectory, true);
    }
  }

  static int GetArchitectureFlags(string path)
  {
    Assert.IsNotNull(GetArchitectures);
    return Convert.ToInt32(GetArchitectures.Invoke(null, new object[] { path }));
  }
}
#endif
