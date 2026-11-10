#if UNITY_EDITOR
using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Behaviors;

public class UserLibraryBehaviorTests
{
  [Test]
  public void UserLibraryBehaviorRoundTripsIntoTheCardLibraryWithoutEscapingItsDirectory()
  {
    string file = "user-library-test-" + Guid.NewGuid().ToString("N") + ".js";
    string originalUserDataDirName = Util.UserDataDirName;
    string testUserDataDirName = Path.Combine(
      "GAMEBUILDER", "Library", "UserLibraryBehaviorTests-" + Guid.NewGuid().ToString("N"));
    Util.UserDataDirName = testUserDataDirName;
    string root = Path.Combine(Util.GetUserDataDir(), "Behaviors");
    string path = Path.Combine(root, file);
    string userDataRoot = Util.GetUserDataDir();
    GameObject owner = new GameObject("User library behavior test");
    owner.SetActive(false);
    BehaviorSystem system = owner.AddComponent<BehaviorSystem>();
    system.behaviorLibraryDirectory = "BehaviorLibrary";

    try
    {
      system.SaveToUserLibrary(file, new Behavior
      {
        javascript = "export function onInit() {}",
        metadataJson = "{\"cardSystemCardData\":{\"isCard\":true,\"title\":\"User library test\"}}"
      });

      string uri = system.UserLibraryFileToUri(file);
      Behavior loaded = system.GetBehaviorData(uri);
      Assert.IsTrue(system.IsBehaviorUriValid(uri));
      Assert.AreEqual(file, loaded.userLibraryFile);
      Assert.AreEqual("export function onInit() {}", loaded.javascript);
      Assert.That(system.LoadBehaviorLibrary(), Does.Contain(uri),
        "User-library behaviors must appear in the Card Library source list.");

      system.SaveToUserLibrary(file, new Behavior { javascript = "export function onTick() {}" });
      loaded = system.GetBehaviorData(uri);
      Assert.AreEqual("export function onTick() {}", loaded.javascript);
      Assert.IsNull(loaded.metadataJson, "Saving without metadata must remove a stale sidecar.");

      Assert.IsFalse(system.IsValidUserLibraryFile("../outside.js"));
      Assert.IsFalse(system.IsBehaviorUriValid("userlib:..%2Foutside.js"));
      Assert.Throws<ArgumentException>(() => system.SaveToUserLibrary("../outside.js", new Behavior()));
    }
    finally
    {
      if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
      Util.UserDataDirName = originalUserDataDirName;
      if (Directory.Exists(userDataRoot)) Directory.Delete(userDataRoot, true);
    }
  }
}
#endif
