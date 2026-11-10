#if UNITY_EDITOR
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using PolyToolkitEditor;
using PolyToolkit;
using UnityEditor;
using UnityEngine;

public class PolyToolkitAssetBrowserLifecycleTests
{
  const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
  const string TitleTexturePath = "Assets/third_party/PolyToolkit/Editor/Textures/PolyToolkitTitle.png";

  [Test]
  public void ClosingUninitializedAssetBrowserReleasesOwnedTextureAndPreservesImportedAssets()
  {
    AssetBrowserWindow window = ScriptableObject.CreateInstance<AssetBrowserWindow>();
    Texture2D loadingTexture = new Texture2D(1, 1);
    Texture2D titleAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(TitleTexturePath);

    try
    {
      Assert.IsNotNull(titleAsset, "The browser title must be an imported project texture.");
      FieldInfo loadingTextureField = typeof(AssetBrowserWindow).GetField("loadingTex", PrivateInstance);
      FieldInfo titleTextureField = typeof(AssetBrowserWindow).GetField("titleTex", PrivateInstance);
      MethodInfo onDestroy = typeof(AssetBrowserWindow).GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(loadingTextureField);
      Assert.IsNotNull(titleTextureField);
      Assert.IsNotNull(onDestroy);
      loadingTextureField.SetValue(window, loadingTexture);
      titleTextureField.SetValue(window, titleAsset);

      Assert.DoesNotThrow(() => onDestroy.Invoke(window, null),
        "Closing the browser before its first GUI initialization must be safe.");
      Assert.IsTrue(loadingTexture == null, "The browser must release its generated loading texture.");
      Assert.AreSame(titleAsset, AssetDatabase.LoadAssetAtPath<Texture2D>(TitleTexturePath),
        "The browser must not destroy its imported title texture.");

      Object.DestroyImmediate(window);
      window = null;
    }
    finally
    {
      if (window != null) Object.DestroyImmediate(window);
      if (loadingTexture != null) Object.DestroyImmediate(loadingTexture);
    }
  }
  [Test]
  public void DisposedManagerReleasesCachedAndLateThumbnails()
  {
    AssetBrowserManager manager = (AssetBrowserManager)FormatterServices.GetUninitializedObject(
      typeof(AssetBrowserManager));
    ThumbnailCache cache = new ThumbnailCache();
    Texture2D cachedTexture = new Texture2D(1, 1);
    Texture2D lateTexture = null;

    try
    {
      FieldInfo cacheField = typeof(AssetBrowserManager).GetField("thumbnailCache", PrivateInstance);
      FieldInfo queryIdField = typeof(AssetBrowserManager).GetField("queryId", PrivateInstance);
      FieldInfo queryingField = typeof(AssetBrowserManager).GetField("querying", PrivateInstance);
      MethodInfo dispose = typeof(AssetBrowserManager).GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public);
      MethodInfo onThumbnailFetched = typeof(AssetBrowserManager).GetMethod("OnThumbnailFetched", PrivateInstance);
      Assert.IsNotNull(cacheField);
      Assert.IsNotNull(queryIdField);
      Assert.IsNotNull(queryingField);
      Assert.IsNotNull(dispose);
      Assert.IsNotNull(onThumbnailFetched);
      cacheField.SetValue(manager, cache);
      queryIdField.SetValue(manager, 4);
      queryingField.SetValue(manager, true);
      cache.Put("cached-asset", cachedTexture);

      dispose.Invoke(manager, null);
      Assert.IsTrue(cachedTexture == null, "Closing the browser must release cached thumbnails.");
      Assert.AreEqual(5, queryIdField.GetValue(manager), "Closing must invalidate pending asset-list requests.");
      Assert.IsFalse((bool)queryingField.GetValue(manager));
      Texture2D foundTexture;
      Assert.IsFalse(cache.TryGet("cached-asset", out foundTexture));
      Assert.DoesNotThrow(() => dispose.Invoke(manager, null), "Manager disposal must be idempotent.");

      lateTexture = new Texture2D(1, 1);
      PolyAsset asset = new PolyAsset { name = "late-asset", thumbnailTexture = lateTexture };
      onThumbnailFetched.Invoke(manager, new object[] { asset, PolyStatus.Success() });
      Assert.IsTrue(lateTexture == null, "A thumbnail received after close must be destroyed.");
      Assert.IsNull(asset.thumbnailTexture, "A disposed manager must clear the late asset reference.");
    }
    finally
    {
      if (cachedTexture != null) Object.DestroyImmediate(cachedTexture);
      if (lateTexture != null) Object.DestroyImmediate(lateTexture);
    }
  }
}
#endif
