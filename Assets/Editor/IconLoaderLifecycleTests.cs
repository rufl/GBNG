#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

public class IconLoaderLifecycleTests
{
  static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
  static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

  [UnityTest]
  public IEnumerator RepeatedLoadsReuseCachedSprite()
  {
    GameObject loaderObject = new GameObject("Icon loader lifecycle test");
    IconLoader loader = loaderObject.AddComponent<IconLoader>();
    Sprite firstSprite = null;
    bool firstCompleted = false;
    string error = null;
    try
    {
      loader.LoadIconSprite("bug_report", (name, sprite) =>
      {
        firstSprite = sprite;
        firstCompleted = true;
      }, name => error = name);

      for (int frame = 0; frame < 120 && !firstCompleted && error == null; frame++)
      {
        yield return null;
      }

      Assert.IsTrue(firstCompleted, "The built-in icon did not finish loading.");
      Assert.IsNotNull(firstSprite);
      Sprite secondSprite = null;
      loader.LoadIconSprite("bug_report", (name, sprite) => secondSprite = sprite);
      Assert.AreSame(firstSprite, secondSprite);
    }
    finally
    {
      FieldInfo cacheField = typeof(IconLoader).GetField("loadedSprites", PrivateInstance);
      if (cacheField != null)
      {
        Dictionary<string, Sprite> sprites = (Dictionary<string, Sprite>)cacheField.GetValue(loader);
        foreach (Sprite sprite in sprites.Values)
        {
          if (sprite == null) continue;
          Texture2D texture = sprite.texture;
          Object.DestroyImmediate(sprite);
          if (texture != null) Object.DestroyImmediate(texture);
        }
      }
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
