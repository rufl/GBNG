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
  [Test]
  public void SaveMenuReleasesOwnedCaptureTexturesButPreservesCallerOwnedTextures()
  {
    GameObject menuObject = new GameObject("Save menu capture texture lifecycle test");
    menuObject.SetActive(false);
    SaveMenu menu = menuObject.AddComponent<SaveMenu>();
    GameObject saveUiObject = new GameObject("Save UI");
    GameObject imageObject = new GameObject("Screenshot image", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
    Texture2D firstCapture = new Texture2D(2, 2);
    Texture2D callerTexture = new Texture2D(2, 2);
    Texture2D finalCapture = new Texture2D(2, 2);
    Sprite firstSprite = null;
    Sprite callerSprite = null;
    Sprite finalSprite = null;
    try
    {
      SaveUI saveUI = saveUiObject.AddComponent<SaveUI>();
      saveUI.screenshotImage = imageObject.GetComponent<Image>();
      FieldInfo saveUIField = typeof(SaveMenu).GetField("saveUI", PrivateInstance);
      Assert.IsNotNull(saveUIField);
      saveUIField.SetValue(menu, saveUI);

      MethodInfo setCurrentImage = typeof(SaveMenu).GetMethod("SetCurrentSaveImage", PrivateInstance);
      MethodInfo setScreenshotSprite = typeof(SaveMenu).GetMethod("SetScreenshotSprite", PrivateInstance);
      Assert.IsNotNull(setCurrentImage);
      Assert.IsNotNull(setScreenshotSprite);
      setCurrentImage.Invoke(menu, new object[] { firstCapture, true });
      setScreenshotSprite.Invoke(menu, new object[] { firstCapture });
      firstSprite = saveUI.screenshotImage.sprite;

      setCurrentImage.Invoke(menu, new object[] { callerTexture, false });
      Assert.IsTrue(firstCapture == null, "Replacing an owned capture must destroy its Texture2D.");
      Assert.IsTrue(firstSprite == null, "Replacing a capture must release its generated Sprite.");
      Assert.IsNotNull(callerTexture, "Replacing a capture must preserve the caller-owned texture.");

      setScreenshotSprite.Invoke(menu, new object[] { callerTexture });
      callerSprite = saveUI.screenshotImage.sprite;
      setCurrentImage.Invoke(menu, new object[] { finalCapture, true });
      setScreenshotSprite.Invoke(menu, new object[] { finalCapture });
      finalSprite = saveUI.screenshotImage.sprite;

      MethodInfo onDestroy = typeof(SaveMenu).GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(onDestroy);
      onDestroy.Invoke(menu, null);
      Assert.IsTrue(finalCapture == null, "Destroying SaveMenu must release its owned capture texture.");
      Assert.IsTrue(finalSprite == null, "Destroying SaveMenu must release its generated Sprite.");
      Assert.IsNotNull(callerTexture, "Destroying SaveMenu must preserve caller-owned textures.");
      Assert.IsTrue(callerSprite == null, "Replacing a screenshot must release the prior generated Sprite.");
    }
    finally
    {
      if (firstSprite != null) Object.DestroyImmediate(firstSprite);
      if (callerSprite != null) Object.DestroyImmediate(callerSprite);
      if (finalSprite != null) Object.DestroyImmediate(finalSprite);
      Object.DestroyImmediate(menuObject);
      Object.DestroyImmediate(saveUiObject);
      Object.DestroyImmediate(imageObject);
      if (firstCapture != null) Object.DestroyImmediate(firstCapture);
      if (callerTexture != null) Object.DestroyImmediate(callerTexture);
      if (finalCapture != null) Object.DestroyImmediate(finalCapture);
    }
  }
  [Test]
  public void RuntimeSpriteCollectionReleasesEverySpriteAndPreservesTextures()
  {
    System.Type collectionType = typeof(GameDetail).Assembly.GetType("RuntimeSpriteCollection");
    Assert.IsNotNull(collectionType);
    object collection = System.Activator.CreateInstance(collectionType, true);
    MethodInfo track = collectionType.GetMethod("Track");
    MethodInfo releaseAll = collectionType.GetMethod("ReleaseAll");
    Assert.IsNotNull(track);
    Assert.IsNotNull(releaseAll);

    Texture2D[] textures = { new Texture2D(2, 2), new Texture2D(2, 2) };
    Sprite[] sprites = new Sprite[textures.Length];
    try
    {
      for (int i = 0; i < textures.Length; i++)
      {
        sprites[i] = Sprite.Create(textures[i], new Rect(0, 0, 2, 2), Vector2.zero);
        Assert.AreSame(sprites[i], track.Invoke(collection, new object[] { sprites[i] }));
      }

      releaseAll.Invoke(collection, null);

      for (int i = 0; i < textures.Length; i++)
      {
        Assert.IsTrue(sprites[i] == null, "Releasing the collection must destroy each owned Sprite.");
        Assert.IsNotNull(textures[i], "The collection must not destroy caller-owned textures.");
      }
      releaseAll.Invoke(collection, null);
    }
    finally
    {
      releaseAll.Invoke(collection, null);
      foreach (Sprite sprite in sprites)
      {
        if (sprite != null) Object.DestroyImmediate(sprite);
      }
      foreach (Texture2D texture in textures)
      {
        if (texture != null) Object.DestroyImmediate(texture);
      }
    }
  }

  [Test]
  public void RuntimeTextureOwnerReleasesReplacedAndFinalOwnedTextures()
  {
    System.Type ownerType = typeof(GameDetail).Assembly.GetType("RuntimeTextureOwner");
    Assert.IsNotNull(ownerType);
    object owner = System.Activator.CreateInstance(ownerType, true);
    MethodInfo replace = ownerType.GetMethod("Replace");
    MethodInfo release = ownerType.GetMethod("Release");
    System.Reflection.PropertyInfo current = ownerType.GetProperty("Current");
    Assert.IsNotNull(replace);
    Assert.IsNotNull(release);
    Assert.IsNotNull(current);
    Texture2D first = new Texture2D(2, 2);
    Texture2D second = new Texture2D(2, 2);
    try
    {
      replace.Invoke(owner, new object[] { first });
      Assert.AreSame(first, current.GetValue(owner));
      replace.Invoke(owner, new object[] { second });
      Assert.IsTrue(first == null, "Replacing an owned texture must destroy the previous texture.");
      Assert.AreSame(second, current.GetValue(owner));

      release.Invoke(owner, null);
      Assert.IsTrue(second == null, "Releasing the owner must destroy its final texture.");
      Assert.IsNull(current.GetValue(owner));
    }
    finally
    {
      release.Invoke(owner, null);
      if (first != null) Object.DestroyImmediate(first);
      if (second != null) Object.DestroyImmediate(second);
    }
  }
  [Test]
  public void ActorUploadDialogReleasesCapturedThumbnailAndKeepsOtherTextures()
  {
    GameObject dialogObject = new GameObject("Actor upload dialog texture lifecycle test");
    dialogObject.SetActive(false);
    ActorPrefabUploadDialog dialog = dialogObject.AddComponent<ActorPrefabUploadDialog>();
    GameObject imageObject = new GameObject("Upload thumbnail", typeof(RectTransform), typeof(CanvasRenderer),
      typeof(UnityEngine.UI.RawImage));
    Texture2D capturedTexture = new Texture2D(2, 2);
    Texture2D replacementTexture = new Texture2D(2, 2);
    Texture2D secondCapture = new Texture2D(2, 2);
    System.Type ownerType = typeof(GameDetail).Assembly.GetType("RuntimeTextureOwner");
    Assert.IsNotNull(ownerType);
    object owner = System.Activator.CreateInstance(ownerType, true);
    MethodInfo replace = ownerType.GetMethod("Replace");
    MethodInfo release = ownerType.GetMethod("Release");
    try
    {
      UnityEngine.UI.RawImage image = imageObject.GetComponent<UnityEngine.UI.RawImage>();
      FieldInfo thumbnailImageField = typeof(WorkshopUploadDialog).GetField("thumbnailImage", PrivateInstance);
      FieldInfo ownedSnapshotTextureField = typeof(ActorPrefabUploadDialog).GetField(
        "ownedSnapshotTexture",
        PrivateInstance);
      Assert.IsNotNull(thumbnailImageField);
      Assert.IsNotNull(ownedSnapshotTextureField);
      Assert.IsNotNull(replace);
      Assert.IsNotNull(release);
      thumbnailImageField.SetValue(dialog, image);
      ownedSnapshotTextureField.SetValue(dialog, owner);

      MethodInfo onDisable = typeof(ActorPrefabUploadDialog).GetMethod("OnDisable", PrivateInstance);
      Assert.IsNotNull(onDisable);
      replace.Invoke(owner, new object[] { capturedTexture });
      image.texture = capturedTexture;
      onDisable.Invoke(dialog, null);
      Assert.IsTrue(capturedTexture == null, "Closing the dialog must release its captured thumbnail.");
      Assert.IsNull(image.texture, "Closing the dialog must clear its released thumbnail reference.");

      image.texture = replacementTexture;
      replace.Invoke(owner, new object[] { secondCapture });
      onDisable.Invoke(dialog, null);
      Assert.IsTrue(secondCapture == null, "Dialog teardown must release its current capture.");
      Assert.AreSame(replacementTexture, image.texture, "Dialog teardown must preserve a different texture.");
      Assert.IsNotNull(replacementTexture);
    }
    finally
    {
      if (release != null) release.Invoke(owner, null);
      Object.DestroyImmediate(dialogObject);
      Object.DestroyImmediate(imageObject);
      if (capturedTexture != null) Object.DestroyImmediate(capturedTexture);
      if (replacementTexture != null) Object.DestroyImmediate(replacementTexture);
      if (secondCapture != null) Object.DestroyImmediate(secondCapture);
    }
  }
  [Test]
  public void PopupReleasesItsGeneratedSpriteAndOwnedSnapshotTexture()
  {
    GameObject popupObject = new GameObject("Snapshot popup", typeof(RectTransform));
    popupObject.SetActive(false);
    Popup popup = popupObject.AddComponent<Popup>();
    GameObject imageObject = new GameObject("Popup image", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
    Texture2D texture = new Texture2D(2, 2);
    Sprite sprite = null;
    try
    {
      popup.image = imageObject.GetComponent<Image>();
      MethodInfo setSprite = typeof(Popup).GetMethod("SetSprite", PrivateInstance);
      Assert.IsNotNull(setSprite);
      setSprite.Invoke(popup, new object[] { texture });
      sprite = popup.image.sprite;

      MethodInfo onDestroy = typeof(Popup).GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(onDestroy);
      onDestroy.Invoke(popup, null);

      Assert.IsTrue(sprite == null, "Destroying the popup must release its generated Sprite.");
      Assert.IsTrue(texture == null, "The popup owns and releases its snapshot texture.");
    }
    finally
    {
      if (sprite != null) Object.DestroyImmediate(sprite);
      Object.DestroyImmediate(popupObject);
      Object.DestroyImmediate(imageObject);
      Object.DestroyImmediate(texture);
    }
  }


  [Test]
  public void IconLoaderDestroysCachedSpritesAndTheirOwnedTextures()
  {
    GameObject loaderObject = new GameObject("Icon loader lifecycle test");
    loaderObject.SetActive(false);
    IconLoader loader = loaderObject.AddComponent<IconLoader>();
    Texture2D texture = new Texture2D(2, 2);
    Sprite sprite = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.zero);
    try
    {
      FieldInfo loadedSpritesField = typeof(IconLoader).GetField("loadedSprites", PrivateInstance);
      Assert.IsNotNull(loadedSpritesField);
      System.Collections.IDictionary loadedSprites =
        (System.Collections.IDictionary)loadedSpritesField.GetValue(loader);
      loadedSprites.Add("test-icon", sprite);

      MethodInfo onDestroy = typeof(IconLoader).GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(onDestroy);
      onDestroy.Invoke(loader, null);

      Assert.IsTrue(sprite == null, "Destroying the icon cache must release cached Sprites.");
      Assert.IsTrue(texture == null, "The icon cache owns and releases textures loaded for those Sprites.");
      Assert.AreEqual(0, loadedSprites.Count);
    }
    finally
    {
      if (sprite != null) Object.DestroyImmediate(sprite);
      if (texture != null) Object.DestroyImmediate(texture);
      Object.DestroyImmediate(loaderObject);
    }
  }
  [Test]
  public void ImageLoaderReleasesCachesAndIgnoresLateWorkshopCallbacksOnTeardown()
  {
    GameObject loaderObject = new GameObject("Image loader lifecycle test");
    loaderObject.SetActive(false);
    ImageLoader loader = loaderObject.AddComponent<ImageLoader>();
    Texture2D texture = new Texture2D(2, 2);
    Sprite sprite = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.zero);
    try
    {
      FieldInfo loadedTexturesField = typeof(ImageLoader).GetField("loadedTextures", PrivateInstance);
      FieldInfo loadedSpritesField = typeof(ImageLoader).GetField("loadedSprites", PrivateInstance);
      FieldInfo pendingRequestsField = typeof(ImageLoader).GetField("pendingRequests", PrivateInstance);
      Assert.IsNotNull(loadedTexturesField);
      Assert.IsNotNull(loadedSpritesField);
      Assert.IsNotNull(pendingRequestsField);
      System.Collections.IDictionary loadedTextures =
        (System.Collections.IDictionary)loadedTexturesField.GetValue(loader);
      System.Collections.IDictionary loadedSprites =
        (System.Collections.IDictionary)loadedSpritesField.GetValue(loader);
      System.Collections.IDictionary pendingRequests =
        (System.Collections.IDictionary)pendingRequestsField.GetValue(loader);
      loadedTextures.Add("sw:test", texture);
      loadedSprites.Add("sw:test", sprite);

      System.Type pendingRequestType = typeof(ImageLoader).GetNestedType("PendingRequest", BindingFlags.NonPublic);
      ConstructorInfo pendingRequestConstructor = pendingRequestType.GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        null,
        new[] { typeof(string), typeof(Image), typeof(System.Action<string, bool>) },
        null);
      Assert.IsNotNull(pendingRequestConstructor);
      pendingRequests.Add("sw:pending", pendingRequestConstructor.Invoke(
        new object[] { "sw:pending", null, null }));

      MethodInfo onDestroy = typeof(ImageLoader).GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(onDestroy);
      onDestroy.Invoke(loader, null);

      Assert.IsTrue(sprite == null, "Destroying the loader must release cached Sprites.");
      Assert.IsTrue(texture == null, "Destroying the loader must release cached image textures.");
      Assert.AreEqual(0, loadedSprites.Count);
      Assert.AreEqual(0, loadedTextures.Count);
      Assert.AreEqual(0, pendingRequests.Count, "Destroying the loader must release in-flight requests.");

      MethodInfo onWorkshopFetchComplete = typeof(ImageLoader).GetMethod(
        "OnWorkshopFetchComplete",
        PrivateInstance);
      Assert.IsNotNull(onWorkshopFetchComplete);
      onWorkshopFetchComplete.Invoke(loader, new object[] {
        "sw:pending",
        Util.Maybe<string>.CreateEmpty()
      });
      Assert.AreEqual(0, loadedSprites.Count, "Late callbacks must not repopulate destroyed caches.");
      Assert.AreEqual(0, loadedTextures.Count, "Late callbacks must not repopulate destroyed caches.");
    }
    finally
    {
      if (sprite != null) Object.DestroyImmediate(sprite);
      if (texture != null) Object.DestroyImmediate(texture);
      Object.DestroyImmediate(loaderObject);
    }
  }
}
#endif
