#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using PolyToolkitEditor;
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
}
#endif
