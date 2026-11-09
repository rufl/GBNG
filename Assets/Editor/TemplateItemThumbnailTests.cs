#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.Networking;

public class TemplateItemThumbnailTests
{
  static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

  [UnityTest]
  public IEnumerator SuccessfulRequestCompletesWhileItemIsInactiveAndKeepsOneSprite()
  {
    GameObject itemObject = new GameObject("Template thumbnail test");
    try
    {
      Image image = itemObject.AddComponent<Image>();
      TemplateItem item = itemObject.AddComponent<TemplateItem>();
      SetPrivateField(item, "thumbnail", image);

      string imagePath = Path.Combine(Application.streamingAssetsPath, "ExampleGames", "Public", "template-empty.png");
      Assert.IsTrue(File.Exists(imagePath), "Missing built-in thumbnail fixture: " + imagePath);
      item.SetThumbnailPath(new Uri(Path.GetFullPath(imagePath)).AbsoluteUri);
      itemObject.SetActive(false);

      yield return WaitForRequestToFinish(item);

      Assert.IsNull(GetRequest(item));
      Assert.IsNotNull(image.sprite);
      Sprite loadedSprite = image.sprite;
      for (int i = 0; i < 3; i++) yield return null;
      Assert.AreSame(loadedSprite, image.sprite, "A completed request must not create a new Sprite each frame.");
    }
    finally
    {
      UnityEngine.Object.DestroyImmediate(itemObject);
    }
  }

  [UnityTest]
  public IEnumerator FailedRequestIsReleasedWithoutAssigningASprite()
  {
    GameObject itemObject = new GameObject("Failed template thumbnail test");
    try
    {
      Image image = itemObject.AddComponent<Image>();
      TemplateItem item = itemObject.AddComponent<TemplateItem>();
      SetPrivateField(item, "thumbnail", image);

      string missingPath = Path.Combine(Application.temporaryCachePath, "missing-template-thumbnail-" + Guid.NewGuid() + ".png");
      item.SetThumbnailPath(new Uri(missingPath).AbsoluteUri);

      yield return WaitForRequestToFinish(item);

      Assert.IsNull(GetRequest(item));
      Assert.IsNull(image.sprite);
    }
    finally
    {
      UnityEngine.Object.DestroyImmediate(itemObject);
    }
  }

  static IEnumerator WaitForRequestToFinish(TemplateItem item)
  {
    for (int frame = 0; frame < 120 && GetRequest(item) != null; frame++)
    {
      yield return null;
    }
  }

  static UnityWebRequest GetRequest(TemplateItem item)
  {
    return (UnityWebRequest)typeof(TemplateItem).GetField("thumbnailWebRequest", PrivateInstance).GetValue(item);
  }

  static void SetPrivateField(TemplateItem item, string fieldName, object value)
  {
    typeof(TemplateItem).GetField(fieldName, PrivateInstance).SetValue(item, value);
  }
}
#endif
