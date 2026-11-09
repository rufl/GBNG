#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class GameDetailThumbnailLifecycleTests
{
  static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

  static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

  [Test]
  public void AssimpDiffuseDecoderDestroysTextureWhenImageBytesAreInvalid()
  {
    System.Type importerType = typeof(GameDetail).Assembly.GetType("AssimpModelLoader");
    Assert.IsNotNull(importerType);
    MethodInfo decode = importerType.GetMethod("DecodeDiffuseTexture", PrivateStatic);
    Assert.IsNotNull(decode);

    string textureName = "invalid-diffuse-texture-" + System.Guid.NewGuid().ToString("N");
    object result = decode.Invoke(null, new object[] {
      new byte[] { 0x47, 0x42, 0x20, 0x49, 0x6e, 0x76, 0x61, 0x6c, 0x69, 0x64 },
      textureName
    });

    Assert.IsNull(result, "Invalid image bytes must not produce an imported texture.");
    bool leakedTextureFound = false;
    foreach (Texture2D texture in Resources.FindObjectsOfTypeAll<Texture2D>())
    {
      if (texture != null && texture.name == textureName)
      {
        leakedTextureFound = true;
        break;
      }
    }
    Assert.IsFalse(leakedTextureFound, "Rejected image data must not leave a live Texture2D.");
  }

  [Test]
  public void AssimpMeshBuilderDestroysPartialMeshWhenImportDataIsInvalid()
  {
    System.Type importerType = typeof(GameDetail).Assembly.GetType("AssimpModelLoader");
    Assert.IsNotNull(importerType);
    MethodInfo createMesh = importerType.GetMethod("CreateMesh", PrivateStatic);
    Assert.IsNotNull(createMesh);
    Mesh[] existingMeshes = Resources.FindObjectsOfTypeAll<Mesh>();
    int leakedMeshes = 0;
    try
    {
      TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
        () => createMesh.Invoke(null, new object[] { null }));
      Assert.IsInstanceOf<System.NullReferenceException>(exception.InnerException);

      foreach (Mesh mesh in Resources.FindObjectsOfTypeAll<Mesh>())
      {
        if (mesh == null) continue;
        bool existedBeforeCall = false;
        foreach (Mesh existing in existingMeshes)
        {
          if (object.ReferenceEquals(existing, mesh))
          {
            existedBeforeCall = true;
            break;
          }
        }
        if (!existedBeforeCall)
        {
          leakedMeshes++;
          UnityEngine.Object.DestroyImmediate(mesh);
        }
      }

      Assert.AreEqual(0, leakedMeshes, "A failed mesh conversion must release its partial Mesh.");
    }
    finally
    {
      foreach (Mesh mesh in Resources.FindObjectsOfTypeAll<Mesh>())
      {
        if (mesh == null) continue;
        bool existedBeforeCall = false;
        foreach (Mesh existing in existingMeshes)
        {
          if (object.ReferenceEquals(existing, mesh))
          {
            existedBeforeCall = true;
            break;
          }
        }
        if (!existedBeforeCall) UnityEngine.Object.DestroyImmediate(mesh);
      }
    }
  }

  [Test]
  public void AssimpRollbackReleasesMaterialAndItsImportedTexture()
  {
    System.Type importerType = typeof(GameDetail).Assembly.GetType("AssimpModelLoader");
    Assert.IsNotNull(importerType);
    MethodInfo destroyMaterial = importerType.GetMethod("DestroyImportedMaterial", PrivateStatic);
    Assert.IsNotNull(destroyMaterial);
    Shader shader = Shader.Find("Standard");
    Assert.IsNotNull(shader);

    Material material = new Material(shader);
    Texture2D texture = new Texture2D(1, 1);
    material.SetTexture("_MainTex", texture);
    try
    {
      destroyMaterial.Invoke(null, new object[] { material });
      Assert.IsTrue(material == null, "Rollback must destroy its generated Material.");
      Assert.IsTrue(texture == null, "Rollback must destroy the diffuse Texture2D owned by that Material.");
    }
    finally
    {
      if (material != null) UnityEngine.Object.DestroyImmediate(material);
      if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
    }
  }

  [Test]
  public void CachedModelResourcesOutliveMasterUntilLastCloneIsReleased()
  {
    System.Type resourceSetType = typeof(GameDetail).Assembly.GetType("RuntimeModelResourceSet");
    Assert.IsNotNull(resourceSetType);
    MethodInfo capture = resourceSetType.GetMethod("Capture", BindingFlags.Static | BindingFlags.Public);
    MethodInfo cloneResourceSet = resourceSetType.GetMethod("Clone", BindingFlags.Instance | BindingFlags.Public);
    MethodInfo releaseOwner = resourceSetType.GetMethod("ReleaseOwner", BindingFlags.Instance | BindingFlags.Public);
    Assert.IsNotNull(capture);
    Assert.IsNotNull(cloneResourceSet);
    Assert.IsNotNull(releaseOwner);

    Shader shader = Shader.Find("Standard");
    Assert.IsNotNull(shader);
    GameObject master = new GameObject("Cached model resource lifetime test");
    master.SetActive(false);
    Mesh renderMesh = new Mesh();
    Mesh colliderMesh = new Mesh();
    Material material = new Material(shader);
    Texture2D texture = new Texture2D(1, 1);
    material.SetTexture("_MainTex", texture);
    master.AddComponent<MeshFilter>().sharedMesh = renderMesh;
    master.AddComponent<MeshRenderer>().sharedMaterial = material;
    master.AddComponent<MeshCollider>().sharedMesh = colliderMesh;
    object resources = null;
    GameObject clone = null;
    GameObject offstageClone = null;
    try
    {
      resources = capture.Invoke(null, new object[] { master });
      clone = (GameObject)cloneResourceSet.Invoke(resources, new object[] { master });
      Assert.IsNotNull(clone);
      offstageClone = (GameObject)cloneResourceSet.Invoke(resources, new object[] { clone });
      Assert.IsNotNull(offstageClone);
      Assert.AreSame(renderMesh, offstageClone.GetComponent<MeshFilter>().sharedMesh);
      Assert.AreSame(colliderMesh, offstageClone.GetComponent<MeshCollider>().sharedMesh);
      Assert.AreSame(material, offstageClone.GetComponent<MeshRenderer>().sharedMaterial);
      Assert.AreSame(renderMesh, clone.GetComponent<MeshFilter>().sharedMesh);
      Assert.AreSame(colliderMesh, clone.GetComponent<MeshCollider>().sharedMesh);
      Assert.AreSame(material, clone.GetComponent<MeshRenderer>().sharedMaterial);

      releaseOwner.Invoke(resources, null);
      Assert.IsTrue(renderMesh != null, "The cached master may release its ownership while a clone still uses the mesh.");
      Assert.IsTrue(colliderMesh != null, "A clone must retain the shared collider mesh.");
      Assert.IsTrue(material != null && texture != null, "A clone must retain its shared material and diffuse texture.");

      AssetCache.ReleaseAssetClone(clone);
      clone = null;
      Assert.IsTrue(renderMesh != null, "The first clone must retain resources still used by the offstage clone.");
      Assert.IsTrue(colliderMesh != null, "The offstage clone must retain its shared collider mesh.");
      Assert.IsTrue(material != null && texture != null, "The offstage clone must retain its shared material and texture.");

      AssetCache.ReleaseAssetClone(offstageClone);
      offstageClone = null;
      Assert.IsTrue(renderMesh == null, "The render mesh must be freed after the final clone is released.");
      Assert.IsTrue(colliderMesh == null, "The collider mesh must be freed after the final clone is released.");
      Assert.IsTrue(material == null && texture == null, "The material and texture must be freed after the final clone is released.");
    }
    finally
    {
      if (clone != null) AssetCache.ReleaseAssetClone(clone);
      if (offstageClone != null) AssetCache.ReleaseAssetClone(offstageClone);
      if (resources != null) releaseOwner.Invoke(resources, null);
      if (master != null) UnityEngine.Object.DestroyImmediate(master);
      if (renderMesh != null) UnityEngine.Object.DestroyImmediate(renderMesh);
      if (colliderMesh != null) UnityEngine.Object.DestroyImmediate(colliderMesh);
      if (material != null) UnityEngine.Object.DestroyImmediate(material);
      if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
    }
  }
  [Test]
  public void PreviewAndAvatarMaterialInstancesAreReleasedOnOwnerTeardown()
  {
    Shader shader = Shader.Find("Standard");
    Assert.IsNotNull(shader);

    GameObject previewObject = new GameObject("Create tool preview material owner");
    GameObject spawnObject = new GameObject("Spawn preview material owner");
    GameObject avatarObject = new GameObject("Avatar material owner");
    previewObject.SetActive(false);
    spawnObject.SetActive(false);
    avatarObject.SetActive(false);

    Material previewMaterial = new Material(shader);
    Material spawnMaterial = new Material(shader);
    Material[] avatarMaterials = new Material[6];
    try
    {
      CreateToolPreview preview = previewObject.AddComponent<CreateToolPreview>();
      SpawnActor spawn = spawnObject.AddComponent<SpawnActor>();
      AvatarMaterialControl avatar = avatarObject.AddComponent<AvatarMaterialControl>();

      FieldInfo previewMaterialField = typeof(CreateToolPreview).GetField("materialCopy", PrivateInstance);
      FieldInfo spawnMaterialField = typeof(SpawnActor).GetField("spawnMaterialInstance", PrivateInstance);
      Assert.IsNotNull(previewMaterialField);
      Assert.IsNotNull(spawnMaterialField);
      previewMaterialField.SetValue(preview, previewMaterial);
      spawnMaterialField.SetValue(spawn, spawnMaterial);

      string[] avatarMaterialFields = {
        "avatarMaterialInstance",
        "avatarEyesMaterialInstance",
        "avatarHoloMaterialInstance",
        "avatarEyesHoloMaterialInstance"
      };
      for (int i = 0; i < avatarMaterialFields.Length; i++)
      {
        FieldInfo field = typeof(AvatarMaterialControl).GetField(avatarMaterialFields[i], PrivateInstance);
        Assert.IsNotNull(field);
        avatarMaterials[i] = new Material(shader);
        field.SetValue(avatar, avatarMaterials[i]);
      }

      avatarMaterials[4] = new Material(shader);
      avatarMaterials[5] = new Material(shader);
      FieldInfo effectMaterialsField = typeof(AvatarMaterialControl).GetField(
        "hologramEffectMaterialInstances", PrivateInstance);
      Assert.IsNotNull(effectMaterialsField);
      effectMaterialsField.SetValue(avatar, new[] { avatarMaterials[4], avatarMaterials[5] });

      MethodInfo previewOnDestroy = typeof(CreateToolPreview).GetMethod("OnDestroy", PrivateInstance);
      MethodInfo spawnOnDestroy = typeof(SpawnActor).GetMethod("OnDestroy", PrivateInstance);
      MethodInfo avatarOnDestroy = typeof(AvatarMaterialControl).GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(previewOnDestroy);
      Assert.IsNotNull(spawnOnDestroy);
      Assert.IsNotNull(avatarOnDestroy);
      previewOnDestroy.Invoke(preview, null);
      spawnOnDestroy.Invoke(spawn, null);
      avatarOnDestroy.Invoke(avatar, null);

      Assert.IsTrue(previewMaterial == null, "CreateToolPreview must release its owned material.");
      Assert.IsTrue(spawnMaterial == null, "SpawnActor must release its owned material.");
      for (int i = 0; i < avatarMaterials.Length; i++)
      {
        Assert.IsTrue(avatarMaterials[i] == null, "AvatarMaterialControl must release every owned material.");
      }
    }
    finally
    {
      Object.DestroyImmediate(previewObject);
      Object.DestroyImmediate(spawnObject);
      Object.DestroyImmediate(avatarObject);
      if (previewMaterial != null) Object.DestroyImmediate(previewMaterial);
      if (spawnMaterial != null) Object.DestroyImmediate(spawnMaterial);
      foreach (Material material in avatarMaterials)
      {
        if (material != null) Object.DestroyImmediate(material);
      }
    }
  }
  [Test]
  public void GrassSpawnReleasesMaterialsAndUnregistersFromBenderOnDestroy()
  {
    Shader shader = Shader.Find("Standard");
    Assert.IsNotNull(shader);

    GameObject grassSpawnObject = new GameObject("Grass spawn material owner");
    GameObject benderObject = new GameObject("Grass bender material registry");
    grassSpawnObject.SetActive(false);
    benderObject.SetActive(false);
    Material[] materials = { new Material(shader), new Material(shader) };
    try
    {
      GrassSpawn grassSpawn = grassSpawnObject.AddComponent<GrassSpawn>();
      GrassBend bender = benderObject.AddComponent<GrassBend>();

      FieldInfo materialsField = typeof(GrassSpawn).GetField("materialInstances", PrivateInstance);
      FieldInfo benderField = typeof(GrassSpawn).GetField("bender", PrivateInstance);
      FieldInfo registeredSetsField = typeof(GrassBend).GetField("materialSets", PrivateInstance);
      Assert.IsNotNull(materialsField);
      Assert.IsNotNull(benderField);
      Assert.IsNotNull(registeredSetsField);
      materialsField.SetValue(grassSpawn, materials);
      benderField.SetValue(grassSpawn, bender);
      bender.AddMaterials(materials);

      MethodInfo onDestroy = typeof(GrassSpawn).GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(onDestroy);
      onDestroy.Invoke(grassSpawn, null);

      Assert.IsTrue(materials[0] == null && materials[1] == null,
        "GrassSpawn must destroy every material instance it created.");
      var registeredSets =
        (System.Collections.Generic.HashSet<Material[]>)registeredSetsField.GetValue(bender);
      Assert.IsFalse(registeredSets.Contains(materials),
        "GrassBend must not retain a destroyed GrassSpawn material set.");
    }
    finally
    {
      Object.DestroyImmediate(grassSpawnObject);
      Object.DestroyImmediate(benderObject);
      foreach (Material material in materials)
      {
        if (material != null) Object.DestroyImmediate(material);
      }
    }
  }
  [Test]
  public void ToolEffectControllerReleasesTintMaterialsOnTeardown()
  {
    Shader shader = Shader.Find("Standard");
    Assert.IsNotNull(shader);

    GameObject effectObject = new GameObject("Tool effect material owner");
    effectObject.SetActive(false);
    Material[] rendererMaterials = {
      new Material(shader),
      new Material(shader),
      new Material(shader)
    };
    Material particleMaterial = new Material(shader);
    try
    {
      ToolEffectController effect = effectObject.AddComponent<ToolEffectController>();
      FieldInfo tintMaterialsField = typeof(ToolEffectController).GetField("tintMaterialInstances", PrivateInstance);
      FieldInfo particleMaterialField = typeof(ToolEffectController).GetField("particleMaterialInstance", PrivateInstance);
      Assert.IsNotNull(tintMaterialsField);
      Assert.IsNotNull(particleMaterialField);
      tintMaterialsField.SetValue(effect, rendererMaterials);
      particleMaterialField.SetValue(effect, particleMaterial);

      MethodInfo onDestroy = typeof(ToolEffectController).GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(onDestroy);
      onDestroy.Invoke(effect, null);

      foreach (Material material in rendererMaterials)
      {
        Assert.IsTrue(material == null, "ToolEffectController must release every tint material.");
      }
      Assert.IsTrue(particleMaterial == null,
        "ToolEffectController must release its particle renderer material.");
    }
    finally
    {
      Object.DestroyImmediate(effectObject);
      foreach (Material material in rendererMaterials)
      {
        if (material != null) Object.DestroyImmediate(material);
      }
      if (particleMaterial != null) Object.DestroyImmediate(particleMaterial);
    }
  }







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
