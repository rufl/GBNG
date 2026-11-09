#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class IconLoaderLifecycleTests
{
  static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
  static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

  [Test]
  public void RepeatedLoadsReuseCachedSprite()
  {
    GameObject loaderObject = new GameObject("Icon loader lifecycle test");
    loaderObject.SetActive(false);
    IconLoader loader = loaderObject.AddComponent<IconLoader>();
    Texture2D firstTexture = new Texture2D(2, 2);
    Texture2D duplicateTexture = new Texture2D(2, 2);
    Sprite cachedSprite = null;
    try
    {
      MethodInfo getOrCreateSprite = typeof(IconLoader).GetMethod("GetOrCreateSprite", PrivateInstance);
      Assert.IsNotNull(getOrCreateSprite);
      cachedSprite = (Sprite)getOrCreateSprite.Invoke(loader, new object[] { "bug_report", firstTexture });
      Sprite duplicateResult = (Sprite)getOrCreateSprite.Invoke(loader, new object[] { "bug_report", duplicateTexture });

      Assert.AreSame(cachedSprite, duplicateResult);
      Assert.AreSame(firstTexture, cachedSprite.texture);
      Assert.IsTrue(duplicateTexture == null, "A duplicate icon load must release its unused texture.");
      Sprite deliveredSprite = null;
      loader.LoadIconSprite("bug_report", (name, sprite) => deliveredSprite = sprite);
      Assert.AreSame(cachedSprite, deliveredSprite);
    }
    finally
    {
      if (cachedSprite != null) Object.DestroyImmediate(cachedSprite);
      if (firstTexture != null) Object.DestroyImmediate(firstTexture);
      if (duplicateTexture != null) Object.DestroyImmediate(duplicateTexture);
      Object.DestroyImmediate(loaderObject);
    }
  }

  [Test]
  public void DestroyedPickerTargetIgnoresLateIconResult()
  {
    GameObject imageObject = new GameObject("Destroyed icon target test", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
    Texture2D texture = new Texture2D(2, 2);
    Sprite sprite = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.zero);
    try
    {
      Image image = imageObject.GetComponent<Image>();
      MethodInfo assignIcon = typeof(IconPickerDialog).GetMethod("TryAssignLoadedIcon", PrivateStatic);
      Assert.IsNotNull(assignIcon);
      Assert.IsTrue((bool)assignIcon.Invoke(null, new object[] { image, sprite }));
      Assert.AreSame(sprite, image.sprite);

      Object.DestroyImmediate(imageObject);
      Assert.IsFalse((bool)assignIcon.Invoke(null, new object[] { image, sprite }));
    }
    finally
    {
      if (sprite != null) Object.DestroyImmediate(sprite);
      if (texture != null) Object.DestroyImmediate(texture);
      if (imageObject != null) Object.DestroyImmediate(imageObject);
    }
  }
}
#endif
