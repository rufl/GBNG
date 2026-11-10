#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

public class UserDataDirectoryTests
{
  static readonly MethodInfo ResolveWritableUserDocumentsPath = typeof(Util).GetMethod(
    "ResolveWritableUserDocumentsPath", BindingFlags.Static | BindingFlags.NonPublic);

  [Test]
  public void UserDataResolverPrefersWritablePathAndFallsBackFromBlockedPath()
  {
    string tempDirectory = Path.Combine(Path.GetTempPath(), "GameBuilderUserData-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
      string preferredPath = Path.Combine(tempDirectory, "preferred");
      string fallbackPath = Path.Combine(tempDirectory, "fallback");
      Assert.AreEqual(preferredPath, Resolve(preferredPath, fallbackPath));
      Assert.IsTrue(Directory.Exists(Path.Combine(preferredPath, Util.UserDataDirName)));

      string blockedPath = Path.Combine(tempDirectory, "blocked");
      File.WriteAllText(blockedPath, "not a directory");
      Assert.AreEqual(fallbackPath, Resolve(blockedPath, fallbackPath));
      Assert.IsTrue(Directory.Exists(Path.Combine(fallbackPath, Util.UserDataDirName)));
    }
    finally
    {
      Directory.Delete(tempDirectory, true);
    }
  }

  static string Resolve(string preferredPath, string fallbackPath)
  {
    Assert.IsNotNull(ResolveWritableUserDocumentsPath);
    return (string)ResolveWritableUserDocumentsPath.Invoke(null, new object[] { preferredPath, fallbackPath });
  }
}
#endif
