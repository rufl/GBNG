#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class ImageLoaderSpriteCacheTests
{
  static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

  [Test]
  public void RepeatedRequestsForLoadedUrlReuseSprite()
  {
    const string url = "sw:123";
    GameObject loaderObject = new GameObject("Image loader sprite cache test");
    loaderObject.SetActive(false);
    GameObject firstImageObject = new GameObject("First image", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
    GameObject secondImageObject = new GameObject("Second image", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
    Texture2D texture = new Texture2D(2, 2);
    try
    {
      ImageLoader loader = loaderObject.AddComponent<ImageLoader>();
      FieldInfo loadedTexturesField = typeof(ImageLoader).GetField("loadedTextures", PrivateInstance);
      Assert.IsNotNull(loadedTexturesField);
      Dictionary<string, Texture2D> loadedTextures =
        (Dictionary<string, Texture2D>)loadedTexturesField.GetValue(loader);
      loadedTextures.Add(url, texture);

      Image firstImage = firstImageObject.GetComponent<Image>();
      Image secondImage = secondImageObject.GetComponent<Image>();
      loader.Request(url, firstImage);
      loader.Request(url, secondImage);

      Assert.IsNotNull(firstImage.sprite);
      Assert.AreSame(firstImage.sprite, secondImage.sprite);
    }
    finally
    {
      Image firstImage = firstImageObject.GetComponent<Image>();
      Image secondImage = secondImageObject.GetComponent<Image>();
      Sprite firstSprite = firstImage != null ? firstImage.sprite : null;
      Sprite secondSprite = secondImage != null ? secondImage.sprite : null;
      if (firstSprite != null) Object.DestroyImmediate(firstSprite);
      if (secondSprite != null && secondSprite != firstSprite) Object.DestroyImmediate(secondSprite);
      Object.DestroyImmediate(loaderObject);
      Object.DestroyImmediate(firstImageObject);
      Object.DestroyImmediate(secondImageObject);
      Object.DestroyImmediate(texture);
    }
  }
}
#endif
