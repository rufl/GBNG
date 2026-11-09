#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class GameDetailThumbnailLifecycleTests
{
  static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

  [Test]
  public void ReplacingAndReleasingThumbnailSpritesPreservesSourceTextures()
  {
    GameObject detailObject = new GameObject("Game detail thumbnail lifecycle test");
    detailObject.SetActive(false);
    GameDetail detail = detailObject.AddComponent<GameDetail>();
    GameObject imageObject = new GameObject("Thumbnail image", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
    Texture2D firstTexture = new Texture2D(2, 2);
    Texture2D secondTexture = new Texture2D(2, 2);
    Sprite firstSprite = null;
    Sprite secondSprite = null;
    try
    {
      Image image = imageObject.GetComponent<Image>();
      FieldInfo thumbnailImageField = typeof(GameDetail).GetField("thumbnailImage", PrivateInstance);
      Assert.IsNotNull(thumbnailImageField);
      thumbnailImageField.SetValue(detail, image);

      detail.SetThumbnail(firstTexture);
      firstSprite = image.sprite;
      detail.SetThumbnail(secondTexture);
      secondSprite = image.sprite;

      Assert.AreNotSame(firstSprite, secondSprite);
      Assert.IsTrue(firstSprite == null, "Replacing a thumbnail must release the previous owned Sprite.");
      Assert.AreSame(secondTexture, secondSprite.texture);
      Assert.IsNotNull(firstTexture, "The thumbnail Texture2D remains owned by its caller.");

      MethodInfo onDestroy = typeof(GameDetail).GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(onDestroy);
      onDestroy.Invoke(detail, null);
      Assert.IsTrue(secondSprite == null, "Destroying GameDetail must release its current owned Sprite.");
      Assert.IsNotNull(secondTexture, "Releasing a Sprite must not destroy its source Texture2D.");
    }
    finally
    {
      if (firstSprite != null) Object.DestroyImmediate(firstSprite);
      if (secondSprite != null) Object.DestroyImmediate(secondSprite);
      Object.DestroyImmediate(detailObject);
      Object.DestroyImmediate(imageObject);
      Object.DestroyImmediate(firstTexture);
      Object.DestroyImmediate(secondTexture);
    }
  }
  [Test]
  public void SaveMenuReplacesAndReleasesOwnedScreenshotSpritesPreservingSourceTextures()
  {
    GameObject menuObject = new GameObject("Save menu thumbnail lifecycle test");
    menuObject.SetActive(false);
    SaveMenu menu = menuObject.AddComponent<SaveMenu>();
    GameObject saveUiObject = new GameObject("Save UI");
    GameObject imageObject = new GameObject("Screenshot image", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
    Texture2D firstTexture = new Texture2D(2, 2);
    Texture2D secondTexture = new Texture2D(2, 2);
    Sprite firstSprite = null;
    Sprite secondSprite = null;
    try
    {
      SaveUI saveUI = saveUiObject.AddComponent<SaveUI>();
      saveUI.screenshotImage = imageObject.GetComponent<Image>();
      FieldInfo saveUIField = typeof(SaveMenu).GetField("saveUI", PrivateInstance);
      Assert.IsNotNull(saveUIField);
      saveUIField.SetValue(menu, saveUI);

      MethodInfo setScreenshotSprite = typeof(SaveMenu).GetMethod("SetScreenshotSprite", PrivateInstance);
      Assert.IsNotNull(setScreenshotSprite);
      setScreenshotSprite.Invoke(menu, new object[] { firstTexture });
      firstSprite = saveUI.screenshotImage.sprite;
      setScreenshotSprite.Invoke(menu, new object[] { secondTexture });
      secondSprite = saveUI.screenshotImage.sprite;

      Assert.AreNotSame(firstSprite, secondSprite);
      Assert.IsTrue(firstSprite == null, "Replacing a screenshot must release the previous owned Sprite.");
      Assert.AreSame(secondTexture, secondSprite.texture);
      Assert.IsNotNull(firstTexture, "The screenshot Texture2D remains owned by its caller.");

      MethodInfo onDestroy = typeof(SaveMenu).GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(onDestroy);
      onDestroy.Invoke(menu, null);
      Assert.IsTrue(secondSprite == null, "Destroying SaveMenu must release its current owned Sprite.");
      Assert.IsNotNull(secondTexture, "Releasing a Sprite must not destroy its source Texture2D.");
    }
    finally
    {
      if (firstSprite != null) Object.DestroyImmediate(firstSprite);
      if (secondSprite != null) Object.DestroyImmediate(secondSprite);
      Object.DestroyImmediate(menuObject);
      Object.DestroyImmediate(saveUiObject);
      Object.DestroyImmediate(imageObject);
      Object.DestroyImmediate(firstTexture);
      Object.DestroyImmediate(secondTexture);
    }
  }
}
#endif
