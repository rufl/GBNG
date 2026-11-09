#if UNITY_EDITOR
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class AssetSearchStaleResultTests
{
  static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

  [UnityTest]
  public IEnumerator ClearedImageSearchResultsAreIgnoredAndReleased()
  {
    System.Type[] libraryTypes =
    {
      typeof(RenderableLibrary),
      typeof(CreationLibrarySidebar)
    };

    foreach (System.Type libraryType in libraryTypes)
    {
      GameObject libraryObject = new GameObject(libraryType.Name + " stale search test");
      libraryObject.SetActive(false);
      Component library = libraryObject.AddComponent(libraryType);
      Texture2D staleThumbnail = new Texture2D(1, 1);

      try
      {
        FieldInfo generationField = libraryType.GetField("searchGeneration", PrivateInstance);
        FieldInfo webResultsField = libraryType.GetField("webResults", PrivateInstance);
        MethodInfo clearAssets = libraryType.GetMethod("ClearAssets", PrivateInstance);
        MethodInfo onWebSearchResult = libraryType.GetMethod("OnWebSearchResult", PrivateInstance);
        Assert.IsNotNull(generationField);
        Assert.IsNotNull(webResultsField);
        Assert.IsNotNull(clearAssets);
        Assert.IsNotNull(onWebSearchResult);

        int staleGeneration = (int)generationField.GetValue(library);
        clearAssets.Invoke(library, null);
        Assert.AreEqual(staleGeneration + 1, generationField.GetValue(library));

        ActorableSearchResult staleResult = new ActorableSearchResult();
        staleResult.renderableReference.assetType = AssetType.Image;
        staleResult.thumbnail = staleThumbnail;
        onWebSearchResult.Invoke(library, new object[] { staleGeneration, staleResult });

        System.Collections.ICollection webResults =
          (System.Collections.ICollection)webResultsField.GetValue(library);
        Assert.AreEqual(0, webResults.Count, "A result from the cleared search must not appear.");

        yield return null;
        Assert.IsTrue(staleThumbnail == null, "The discarded thumbnail texture must be destroyed.");
        MethodInfo onDestroy = libraryType.GetMethod("OnDestroy", PrivateInstance);
        Assert.IsNotNull(onDestroy);
        int generationBeforeDestroy = (int)generationField.GetValue(library);
        Texture2D lateThumbnail = new Texture2D(1, 1);
        try
        {
          ActorableSearchResult lateResult = new ActorableSearchResult();
          lateResult.renderableReference.assetType = AssetType.Image;
          lateResult.thumbnail = lateThumbnail;

          onDestroy.Invoke(library, null);
          Assert.AreEqual(generationBeforeDestroy + 1, generationField.GetValue(library));
          onWebSearchResult.Invoke(library, new object[] { generationBeforeDestroy, lateResult });
          Assert.IsTrue(lateThumbnail == null, "A result arriving after library teardown must be released.");
        }
        finally
        {
          if (lateThumbnail != null) Object.DestroyImmediate(lateThumbnail);
        }
      }
      finally
      {
        if (staleThumbnail != null) Object.DestroyImmediate(staleThumbnail);
        Object.DestroyImmediate(libraryObject);
      }
    }
  }
}
#endif
