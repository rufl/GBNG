#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class TransientThumbnailLifecycleTests
{
  const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
  const BindingFlags InstanceMethods = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

  [Test]
  public void TerrainImportFeedbackReleasesItsSpriteAndOwnedTexture()
  {
    GameObject owner = new GameObject("Terrain import thumbnail test");
    Image image = owner.AddComponent<Image>();
    ProgressBlockStyleUI progress = owner.AddComponent<ProgressBlockStyleUI>();
    FieldInfo imageField = typeof(ProgressBlockStyleUI).GetField("image", PrivateInstance);
    Assert.IsNotNull(imageField);
    imageField.SetValue(progress, image);

    Texture2D firstTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
    Texture2D secondTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
    Sprite firstSprite = null;
    Sprite secondSprite = null;

    try
    {
      progress.SetOwnedThumbnail(firstTexture);
      firstSprite = image.sprite;
      progress.SetOwnedThumbnail(secondTexture);
      secondSprite = image.sprite;

      Assert.IsTrue(firstTexture == null, "Replacing the import preview must release its owned texture.");
      Assert.IsTrue(firstSprite == null, "Replacing the import preview must release its generated Sprite.");
      Assert.AreSame(secondTexture, secondSprite.texture);

      Invoke(progress, "OnDestroy");
      UnityEngine.Object.DestroyImmediate(owner);
      Assert.IsTrue(secondTexture == null, "Destroying the progress item must release its owned texture.");
      Assert.IsTrue(secondSprite == null, "Destroying the progress item must release its generated Sprite.");
      secondTexture = null;
      secondSprite = null;
    }
    finally
    {
      if (firstSprite != null) UnityEngine.Object.DestroyImmediate(firstSprite);
      if (secondSprite != null) UnityEngine.Object.DestroyImmediate(secondSprite);
      if (firstTexture != null) UnityEngine.Object.DestroyImmediate(firstTexture);
      if (secondTexture != null) UnityEngine.Object.DestroyImmediate(secondTexture);
      if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
    }
  }

  [Test]
  public void GameBundleLookupsShareThumbnailsAndReleaseDeletedAndCurrentTextures()
  {
    string relativeRoot = "GameBuilderBundleThumbnailTest-" + Guid.NewGuid().ToString("N");
    string absoluteRoot = Path.Combine(Util.AbstractLocation.PersistentData.GetConcretePath(), relativeRoot);
    string deletedBundleId = "deleted-" + Guid.NewGuid().ToString("N");
    string currentBundleId = "current-" + Guid.NewGuid().ToString("N");
    GameObject owner = new GameObject("Game bundle thumbnail test");
    owner.SetActive(false);
    GameBuilder.GameBundleLibrary library = owner.AddComponent<GameBuilder.GameBundleLibrary>();
    FieldInfo locationField = typeof(GameBuilder.GameBundleLibrary).GetField("location", PrivateInstance);
    Assert.IsNotNull(locationField);
    locationField.SetValue(library, new Util.AbstractPath
    {
      root = Util.AbstractLocation.PersistentData,
      relativePath = relativeRoot
    });

    Texture2D deletedThumbnail = null;
    Texture2D currentThumbnail = null;
    try
    {
      WriteBundleThumbnail(Path.Combine(absoluteRoot, deletedBundleId, "thumbnail.png"), Color.cyan);
      GameBuilder.GameBundle oldBundle = library.GetBundle(deletedBundleId);
      deletedThumbnail = oldBundle.GetThumbnail();
      Texture2D repeated = library.GetBundle(deletedBundleId).GetThumbnail();
      Assert.AreSame(deletedThumbnail, repeated,
        "Separate GameBundle lookups must borrow one library-owned decoded texture.");

      library.DeletePermanently(deletedBundleId);
      Assert.IsTrue(deletedThumbnail != null,
        "A deleted bundle thumbnail stays alive while existing UI may still reference it.");
      Assert.IsNull(oldBundle.GetThumbnail(), "A stale bundle handle must not reload a deleted image.");

      WriteBundleThumbnail(Path.Combine(absoluteRoot, currentBundleId, "thumbnail.png"), Color.magenta);
      currentThumbnail = library.GetBundle(currentBundleId).GetThumbnail();
      Assert.IsNotNull(currentThumbnail);

      Invoke(library, "ReleaseThumbnailTextures");
      Assert.IsTrue(deletedThumbnail == null, "Library teardown must release retired bundle thumbnails.");
      Assert.IsTrue(currentThumbnail == null, "Library teardown must release current bundle thumbnails.");
      deletedThumbnail = null;
      currentThumbnail = null;
    }
    finally
    {
      Invoke(library, "ReleaseThumbnailTextures");
      if (deletedThumbnail != null) UnityEngine.Object.DestroyImmediate(deletedThumbnail);
      if (currentThumbnail != null) UnityEngine.Object.DestroyImmediate(currentThumbnail);
      UnityEngine.Object.DestroyImmediate(owner);
      if (Directory.Exists(absoluteRoot)) Directory.Delete(absoluteRoot, true);
    }
  }

  static void WriteBundleThumbnail(string path, Color color)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    Texture2D source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
    try
    {
      source.SetPixels(new[] { color, color, color, color });
      source.Apply();
      File.WriteAllBytes(path, source.EncodeToPNG());
    }
    finally
    {
      UnityEngine.Object.DestroyImmediate(source);
    }
  }

  static object Invoke(object target, string methodName)
  {
    MethodInfo method = target.GetType().GetMethod(methodName, InstanceMethods);
    Assert.IsNotNull(method, "Missing lifecycle method: " + methodName);
    return method.Invoke(target, null);
  }
}
#endif
