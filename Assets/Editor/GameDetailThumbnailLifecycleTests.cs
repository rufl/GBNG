#if UNITY_EDITOR
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.TestTools;

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
  public void GameBuilderStageReleasesOwnedMaterialsAndRestoresGlobalSkybox()
  {
    Shader shader = Shader.Find("Standard");
    Assert.IsNotNull(shader);

    GameObject stageObject = new GameObject("Game Builder stage material owner");
    stageObject.SetActive(false);
    GameBuilderStage stage = stageObject.AddComponent<GameBuilderStage>();
    Material previousSkybox = new Material(shader);
    Material ownedSkybox = new Material(shader);
    Material ownedGround = new Material(shader);
    Material baseSkybox = new Material(shader);
    Material baseGround = new Material(shader);
    Material previousRenderSettingsSkybox = RenderSettings.skybox;
    try
    {
      string[] fieldNames = {
        "skyboxBeforeStage",
        "skyboxMaterials",
        "groundMaterials",
        "skyboxMaterialsBase",
        "groundMaterialsBase"
      };
      object[] fieldValues = {
        previousSkybox,
        new[] { ownedSkybox },
        new[] { ownedGround },
        new[] { baseSkybox },
        new[] { baseGround }
      };
      for (int i = 0; i < fieldNames.Length; i++)
      {
        FieldInfo field = typeof(GameBuilderStage).GetField(fieldNames[i], PrivateInstance);
        Assert.IsNotNull(field);
        field.SetValue(stage, fieldValues[i]);
      }
      RenderSettings.skybox = ownedSkybox;

      MethodInfo onDestroy = typeof(GameBuilderStage).GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(onDestroy);
      onDestroy.Invoke(stage, null);

      Assert.AreSame(previousSkybox, RenderSettings.skybox,
        "Destroying a stage must not leave the global skybox pointing at a destroyed instance.");
      Assert.IsTrue(ownedSkybox == null && ownedGround == null,
        "The stage must destroy its cloned skybox and ground materials.");
      Assert.IsTrue(previousSkybox != null && baseSkybox != null && baseGround != null,
        "Stage teardown must preserve caller-owned and serialized source materials.");
    }
    finally
    {
      RenderSettings.skybox = previousRenderSettingsSkybox;
      Object.DestroyImmediate(stageObject);
      if (previousSkybox != null) Object.DestroyImmediate(previousSkybox);
      if (ownedSkybox != null) Object.DestroyImmediate(ownedSkybox);
      if (ownedGround != null) Object.DestroyImmediate(ownedGround);
      if (baseSkybox != null) Object.DestroyImmediate(baseSkybox);
      if (baseGround != null) Object.DestroyImmediate(baseGround);
    }
  }
  [Test]
  public void GameBuilderStagePreservesCallerOwnedTemporarySkyboxOnTeardown()
  {
    Shader shader = Shader.Find("Standard");
    Assert.IsNotNull(shader);

    GameObject stageObject = new GameObject("Game Builder temporary skybox owner");
    stageObject.SetActive(false);
    GameBuilderStage stage = stageObject.AddComponent<GameBuilderStage>();
    Material previousSkybox = new Material(shader);
    Material temporarySkybox = new Material(shader);
    Material ownedSkybox = new Material(shader);
    Material ownedGround = new Material(shader);
    Material previousRenderSettingsSkybox = RenderSettings.skybox;
    try
    {
      string[] fieldNames = { "skyboxBeforeStage", "skyboxMaterials", "groundMaterials" };
      object[] fieldValues = {
        previousSkybox,
        new[] { ownedSkybox },
        new[] { ownedGround }
      };
      for (int i = 0; i < fieldNames.Length; i++)
      {
        FieldInfo field = typeof(GameBuilderStage).GetField(fieldNames[i], PrivateInstance);
        Assert.IsNotNull(field);
        field.SetValue(stage, fieldValues[i]);
      }
      RenderSettings.skybox = temporarySkybox;

      MethodInfo onDestroy = typeof(GameBuilderStage).GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(onDestroy);
      onDestroy.Invoke(stage, null);

      Assert.AreSame(temporarySkybox, RenderSettings.skybox,
        "Stage teardown must leave a caller-owned temporary skybox active.");
      Assert.IsTrue(ownedSkybox == null && ownedGround == null,
        "The stage must still release its own material instances.");
      Assert.IsTrue(temporarySkybox != null && previousSkybox != null,
        "Stage teardown must not destroy caller-owned skybox materials.");
    }
    finally
    {
      RenderSettings.skybox = previousRenderSettingsSkybox;
      Object.DestroyImmediate(stageObject);
      if (previousSkybox != null) Object.DestroyImmediate(previousSkybox);
      if (temporarySkybox != null) Object.DestroyImmediate(temporarySkybox);
      if (ownedSkybox != null) Object.DestroyImmediate(ownedSkybox);
      if (ownedGround != null) Object.DestroyImmediate(ownedGround);
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
  [Test]
  public void JointTintMaterialsAreOwnedAndReleasedForBothJointComponents()
  {
    Shader shader = Shader.Find("Standard");
    Assert.IsNotNull(shader);

    AssertJointTintMaterialsAreOwnedAndReleased(typeof(JointControl), shader);
    AssertJointTintMaterialsAreOwnedAndReleased(typeof(JointFeedback), shader);
  }

  void AssertJointTintMaterialsAreOwnedAndReleased(System.Type componentType, Shader shader)
  {
    GameObject jointObject = new GameObject(componentType.Name + " material owner");
    jointObject.SetActive(false);
    GameObject lineRendererObject = jointObject;
    GameObject childObject = new GameObject("Joint child renderer");
    childObject.transform.SetParent(jointObject.transform, false);
    GameObject externalLineObject = null;
    if (componentType == typeof(JointControl))
    {
      externalLineObject = new GameObject("Joint external line renderer");
      externalLineObject.SetActive(false);
      lineRendererObject = externalLineObject;
    }

    Material sourceMaterial = new Material(shader);
    sourceMaterial.color = Color.white;
    Material lineMaterialInstance = null;
    Material childMaterialInstance = null;
    try
    {
      LineRenderer lineRenderer = lineRendererObject.AddComponent<LineRenderer>();
      lineRenderer.sharedMaterial = sourceMaterial;
      MeshRenderer childRenderer = childObject.AddComponent<MeshRenderer>();
      childRenderer.sharedMaterial = sourceMaterial;

      Component component = jointObject.AddComponent(componentType);
      FieldInfo lineRendererField = componentType.GetField("lineRenderer", PrivateInstance);
      FieldInfo colorField = componentType.GetField("color", PrivateInstance);
      Assert.IsNotNull(lineRendererField);
      Assert.IsNotNull(colorField);
      lineRendererField.SetValue(component, lineRenderer);
      colorField.SetValue(component, Color.magenta);

      MethodInfo awake = componentType.GetMethod("Awake", PrivateInstance);
      MethodInfo onDestroy = componentType.GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(awake);
      Assert.IsNotNull(onDestroy);
      awake.Invoke(component, null);

      lineMaterialInstance = lineRenderer.sharedMaterial;
      childMaterialInstance = childRenderer.sharedMaterial;
      Assert.AreNotSame(sourceMaterial, lineMaterialInstance,
        "Each joint line must use an owned material instance.");
      Assert.AreNotSame(sourceMaterial, childMaterialInstance,
        "Each joint child renderer must use an owned material instance.");
      Assert.AreNotSame(lineMaterialInstance, childMaterialInstance,
        "Tinting one joint renderer must not share an instance with another renderer.");
      Assert.AreEqual(Color.magenta, lineMaterialInstance.color);
      Assert.AreEqual(Color.magenta, childMaterialInstance.color);
      Assert.AreEqual(Color.white, sourceMaterial.color,
        "Tinting must not modify the prefab/shared source material.");

      onDestroy.Invoke(component, null);
      Assert.IsTrue(lineMaterialInstance == null,
        "Joint teardown must release its line material instance.");
      Assert.IsTrue(childMaterialInstance == null,
        "Joint teardown must release each child renderer material instance.");
    }
    finally
    {
      Object.DestroyImmediate(jointObject);
      if (externalLineObject != null) Object.DestroyImmediate(externalLineObject);
      if (sourceMaterial != null) Object.DestroyImmediate(sourceMaterial);
      if (lineMaterialInstance != null) Object.DestroyImmediate(lineMaterialInstance);
      if (childMaterialInstance != null) Object.DestroyImmediate(childMaterialInstance);
    }
  }

  [Test]
  public void SelectionFeedbackOwnsAndReleasesBaseTintMaterial()
  {
    Shader shader = Shader.Find("Standard");
    Assert.IsNotNull(shader);

    GameObject owner = new GameObject("Selection feedback material owner");
    owner.SetActive(false);
    Material sourceMaterial = new Material(shader);
    sourceMaterial.color = Color.white;
    Material materialInstance = null;
    try
    {
      MeshRenderer renderer = owner.AddComponent<MeshRenderer>();
      renderer.sharedMaterial = sourceMaterial;
      SelectionFeedback feedback = owner.AddComponent<SelectionFeedback>();

      FieldInfo rendererField = typeof(SelectionFeedback).GetField("feedbackRenderer", PrivateInstance);
      FieldInfo defaultColorField = typeof(SelectionFeedback).GetField("defaultColor", PrivateInstance);
      FieldInfo selectColorField = typeof(SelectionFeedback).GetField("selectColor", PrivateInstance);
      Assert.IsNotNull(rendererField);
      Assert.IsNotNull(defaultColorField);
      Assert.IsNotNull(selectColorField);
      rendererField.SetValue(feedback, renderer);
      defaultColorField.SetValue(feedback, Color.blue);
      selectColorField.SetValue(feedback, Color.yellow);

      MethodInfo awake = typeof(SelectionFeedback).GetMethod("Awake", PrivateInstance);
      MethodInfo onDestroy = typeof(SelectionFeedback).GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(awake);
      Assert.IsNotNull(onDestroy);
      awake.Invoke(feedback, null);
      owner.SetActive(true);
      materialInstance = renderer.sharedMaterial;
      Assert.AreNotSame(sourceMaterial, materialInstance,
        "Selection feedback must not tint its shared prefab material.");
      Assert.AreEqual(Color.blue, materialInstance.color);
      feedback.SetSelected(true);
      Assert.AreEqual(Color.yellow, materialInstance.color,
        "Selecting the feedback must apply its selection color.");
      feedback.SetSelected(false);
      Assert.AreEqual(Color.blue, materialInstance.color,
        "Deselecting the feedback must restore its default color.");
      feedback.SetColor(Color.magenta);
      Assert.AreEqual(Color.magenta, materialInstance.color);
      Assert.AreEqual(Color.white, sourceMaterial.color);

      onDestroy.Invoke(feedback, null);
      Assert.IsTrue(materialInstance == null,
        "Selection feedback teardown must release its owned material.");
    }
    finally
    {
      if (owner != null) Object.DestroyImmediate(owner);
      if (sourceMaterial != null) Object.DestroyImmediate(sourceMaterial);
      if (materialInstance != null) Object.DestroyImmediate(materialInstance);
    }
  }

  [Test]
  public void AxisGizmosOwnTheirMaterialsAndPreserveTintTransitions()
  {
    Shader shader = Shader.Find("Sully/Manipulator DS Unlit");
    Assert.IsNotNull(shader);

    AssertAxisGizmoMaterialsAreOwnedAndReleased(
      typeof(PositionSelectionFeedback),
      new[] { "xRenderer", "yRenderer", "zRenderer" },
      new[] { "xColor", "yColor", "zColor" },
      new[] { Color.red, Color.green, Color.blue },
      new object[] { MoveTool.GizmoAxis.X, false },
      new[] { Color.magenta, Color.green, Color.blue },
      shader);
    AssertAxisGizmoMaterialsAreOwnedAndReleased(
      typeof(RotateSelectionFeedback),
      new[] { "yawRenderer", "pitchRenderer", "rollRenderer" },
      new[] { "yawColor", "pitchColor", "rollColor" },
      new[] { Color.green, Color.red, Color.cyan },
      new object[] { RotateTool.RotationAxis.Pitch },
      new[] { Color.green, Color.magenta, Color.cyan },
      shader);
  }

  void AssertAxisGizmoMaterialsAreOwnedAndReleased(
    System.Type componentType,
    string[] rendererFieldNames,
    string[] colorFieldNames,
    Color[] axisColors,
    object[] updateAxisArguments,
    Color[] expectedColors,
    Shader shader)
  {
    GameObject owner = new GameObject(componentType.Name + " material owner");
    owner.SetActive(false);
    GameObject[] axisObjects = new GameObject[3];
    Material[] sourceMaterials = new Material[3];
    Material[] materialInstances = new Material[3];
    try
    {
      Renderer[] renderers = new Renderer[3];
      for (int i = 0; i < renderers.Length; i++)
      {
        axisObjects[i] = new GameObject("Gizmo axis " + i);
        axisObjects[i].transform.SetParent(owner.transform, false);
        renderers[i] = axisObjects[i].AddComponent<MeshRenderer>();
        sourceMaterials[i] = new Material(shader);
        Assert.IsTrue(sourceMaterials[i].HasProperty("_MainTint"),
          "The test shader must expose the gizmo tint property.");
        sourceMaterials[i].SetColor("_MainTint", Color.white);
        renderers[i].sharedMaterial = sourceMaterials[i];
      }

      Component feedback = owner.AddComponent(componentType);
      for (int i = 0; i < renderers.Length; i++)
      {
        FieldInfo rendererField = componentType.GetField(rendererFieldNames[i], PrivateInstance);
        FieldInfo colorField = componentType.GetField(colorFieldNames[i], PrivateInstance);
        Assert.IsNotNull(rendererField);
        Assert.IsNotNull(colorField);
        rendererField.SetValue(feedback, renderers[i]);
        colorField.SetValue(feedback, axisColors[i]);
      }
      FieldInfo selectColorField = componentType.GetField("selectColor", PrivateInstance);
      Assert.IsNotNull(selectColorField);
      selectColorField.SetValue(feedback, Color.magenta);
      owner.SetActive(true);

      MethodInfo setup = componentType.GetMethod("Setup", BindingFlags.Instance | BindingFlags.Public);
      MethodInfo updateAxis = componentType.GetMethod("UpdateAxis", PrivateInstance);
      MethodInfo onDestroy = typeof(SelectionFeedback).GetMethod("OnDestroy", PrivateInstance);
      Assert.IsNotNull(setup);
      Assert.IsNotNull(updateAxis);
      Assert.IsNotNull(onDestroy);
      setup.Invoke(feedback, null);

      for (int i = 0; i < renderers.Length; i++)
      {
        materialInstances[i] = renderers[i].sharedMaterial;
        Assert.AreNotSame(sourceMaterials[i], materialInstances[i],
          "Each gizmo axis must use a component-owned material instance.");
      }
      Assert.AreNotSame(materialInstances[0], materialInstances[1]);
      Assert.AreNotSame(materialInstances[1], materialInstances[2]);

      updateAxis.Invoke(feedback, updateAxisArguments);
      for (int i = 0; i < materialInstances.Length; i++)
      {
        Assert.AreEqual(expectedColors[i], materialInstances[i].GetColor("_MainTint"),
          "Axis selection must tint only the active gizmo axis.");
        Assert.AreEqual(Color.white, sourceMaterials[i].GetColor("_MainTint"),
          "Axis tint changes must not modify the source material.");
      }

      onDestroy.Invoke(feedback, null);
      for (int i = 0; i < materialInstances.Length; i++)
      {
        Assert.IsTrue(materialInstances[i] == null,
          "Gizmo teardown must release every axis material instance.");
      }
    }
    finally
    {
      if (owner != null) Object.DestroyImmediate(owner);
      for (int i = 0; i < sourceMaterials.Length; i++)
      {
        if (sourceMaterials[i] != null) Object.DestroyImmediate(sourceMaterials[i]);
        if (materialInstances[i] != null) Object.DestroyImmediate(materialInstances[i]);
      }
    }
  }

  [UnityTest]
  public IEnumerator SelectionFeedbackReleasesMaterialsDuringRuntimeTeardown()
  {
    yield return new EnterPlayMode();

    GameObject selectionOwner = null;
    GameObject gizmoOwner = null;
    Material selectionSource = null;
    Material selectionInstance = null;
    Material[] axisSources = new Material[3];
    Material[] axisInstances = new Material[3];
    bool selectionMaterialReleased = false;
    bool allAxisMaterialsReleased = false;
    bool sharedSourcesPreserved = false;
    try
    {
      Shader shader = Shader.Find("Standard");
      Assert.IsNotNull(shader);

      selectionOwner = new GameObject("Runtime selection feedback material owner");
      MeshRenderer selectionRenderer = selectionOwner.AddComponent<MeshRenderer>();
      selectionSource = new Material(shader);
      selectionRenderer.sharedMaterial = selectionSource;
      SelectionFeedback selection = selectionOwner.AddComponent<SelectionFeedback>();

      FieldInfo feedbackRendererField =
        typeof(SelectionFeedback).GetField("feedbackRenderer", PrivateInstance);
      Assert.IsNotNull(feedbackRendererField);
      feedbackRendererField.SetValue(selection, selectionRenderer);
      MethodInfo awake = typeof(SelectionFeedback).GetMethod("Awake", PrivateInstance);
      Assert.IsNotNull(awake);
      awake.Invoke(selection, null);
      selectionInstance = selectionRenderer.sharedMaterial;

      gizmoOwner = new GameObject("Runtime axis gizmo material owner");
      Renderer[] axisRenderers = new Renderer[3];
      for (int i = 0; i < axisRenderers.Length; i++)
      {
        GameObject axisObject = new GameObject("Runtime gizmo axis " + i);
        axisObject.transform.SetParent(gizmoOwner.transform, false);
        axisRenderers[i] = axisObject.AddComponent<MeshRenderer>();
        axisSources[i] = new Material(shader);
        axisRenderers[i].sharedMaterial = axisSources[i];
      }

      PositionSelectionFeedback gizmo = gizmoOwner.AddComponent<PositionSelectionFeedback>();
      string[] rendererFields = { "xRenderer", "yRenderer", "zRenderer" };
      for (int i = 0; i < axisRenderers.Length; i++)
      {
        FieldInfo rendererField = typeof(PositionSelectionFeedback).GetField(
          rendererFields[i],
          PrivateInstance);
        Assert.IsNotNull(rendererField);
        rendererField.SetValue(gizmo, axisRenderers[i]);
      }
      gizmo.Setup();
      for (int i = 0; i < axisRenderers.Length; i++)
      {
        axisInstances[i] = axisRenderers[i].sharedMaterial;
      }

      Object.Destroy(selectionOwner);
      Object.Destroy(gizmoOwner);
      yield return null;

      selectionMaterialReleased = selectionInstance == null;
      allAxisMaterialsReleased = true;
      sharedSourcesPreserved = selectionSource != null;
      for (int i = 0; i < axisInstances.Length; i++)
      {
        allAxisMaterialsReleased &= axisInstances[i] == null;
        sharedSourcesPreserved &= axisSources[i] != null;
      }
    }
    finally
    {
      if (selectionOwner != null) Object.DestroyImmediate(selectionOwner);
      if (gizmoOwner != null) Object.DestroyImmediate(gizmoOwner);
      if (selectionSource != null) Object.DestroyImmediate(selectionSource);
      if (selectionInstance != null) Object.DestroyImmediate(selectionInstance);
      for (int i = 0; i < axisSources.Length; i++)
      {
        if (axisSources[i] != null) Object.DestroyImmediate(axisSources[i]);
        if (axisInstances[i] != null) Object.DestroyImmediate(axisInstances[i]);
      }
    }

    yield return new ExitPlayMode();
    Assert.IsTrue(selectionMaterialReleased,
      "Runtime destruction must release the base feedback material instance.");
    Assert.IsTrue(allAxisMaterialsReleased,
      "Runtime destruction must release every inherited gizmo material instance.");
    Assert.IsTrue(sharedSourcesPreserved,
      "Runtime teardown must not destroy shared source materials.");
  }
  [UnityTest]
  public IEnumerator TerrainPreviewTintOverridesStayPerRendererAndReleaseOnTeardown()
  {
    yield return new EnterPlayMode();

    GameObject previewOwner = null;
    GameObject terrainOwner = null;
    GameObject blockPrefab = null;
    GameObject messagingOwner = null;
    GameObject engineOwner = null;
    GameObject localPreview = null;
    GameObject redRemotePreview = null;
    GameObject blueRemotePreview = null;
    Material sourceMaterial = null;
    Material previewMaterialInstance = null;
    bool localTintUsesOwnedMaterial = false;
    bool remoteTintsStayIndependent = false;
    bool clearOnlyRemovesSenderPreviews = false;
    bool handlerAndMaterialReleased = false;
    bool previewsReleased = false;
    bool sharedMaterialPreserved = false;

    try
    {
      Shader shader = Shader.Find("Sully/PreviewMaterial");
      Assert.IsNotNull(shader);
      sourceMaterial = new Material(shader);
      sourceMaterial.SetColor("_MainTint", Color.white);

      blockPrefab = new GameObject("Terrain preview test block");
      blockPrefab.SetActive(false);
      blockPrefab.AddComponent<MeshFilter>();
      blockPrefab.AddComponent<MeshRenderer>().sharedMaterial = sourceMaterial;

      terrainOwner = new GameObject("Terrain preview test renderer");
      terrainOwner.SetActive(false);
      TerrainRendering terrainRendering = terrainOwner.AddComponent<TerrainRendering>();
      FieldInfo perColorPrefabsField =
        typeof(TerrainRendering).GetField("perColorPrefabs", PrivateInstance);
      Assert.IsNotNull(perColorPrefabsField);
      perColorPrefabsField.SetValue(terrainRendering, new TerrainRendering.StylePrefabs[]
      {
        new TerrainRendering.StylePrefabs { fullPrefab = blockPrefab }
      });

      messagingOwner = new GameObject("Terrain preview test messaging");
      messagingOwner.SetActive(false);
      UnreliableMessageSystem messaging = messagingOwner.AddComponent<UnreliableMessageSystem>();

      engineOwner = new GameObject("Terrain preview test engine");
      engineOwner.SetActive(false);
      VoosEngine engine = engineOwner.AddComponent<VoosEngine>();

      previewOwner = new GameObject("Terrain preview test owner");
      previewOwner.SetActive(false);
      CreateTerrainPreview preview = previewOwner.AddComponent<CreateTerrainPreview>();
      typeof(CreateTerrainPreview).GetField("previewMaterial", PrivateInstance)
        .SetValue(preview, sourceMaterial);
      typeof(CreateTerrainPreview).GetField("terrainRendering", PrivateInstance)
        .SetValue(preview, terrainRendering);
      typeof(CreateTerrainPreview).GetField("unreliableMessaging", PrivateInstance)
        .SetValue(preview, messaging);
      typeof(CreateTerrainPreview).GetField("engine", PrivateInstance)
        .SetValue(preview, engine);
      previewOwner.SetActive(true);

      Color localTint = new Color(0.1f, 0.8f, 0.9f, 1f);
      preview.SetTint(localTint);
      MethodInfo instantiatePreview =
        typeof(CreateTerrainPreview).GetMethod("InstantiatePreview", PrivateInstance);
      Assert.IsNotNull(instantiatePreview);
      localPreview = (GameObject)instantiatePreview.Invoke(
        preview,
        new object[] { TerrainManager.BlockShape.Full });
      previewMaterialInstance = (Material)typeof(CreateTerrainPreview)
        .GetField("previewMaterialInstance", PrivateInstance)
        .GetValue(preview);
      Renderer localRenderer = localPreview.GetComponentInChildren<Renderer>();
      localTintUsesOwnedMaterial =
        localRenderer.sharedMaterial == previewMaterialInstance &&
        previewMaterialInstance.GetColor("_MainTint") == ArtUtil.GetHologramColor(localTint) &&
        sourceMaterial.GetColor("_MainTint") == Color.white;

      MethodInfo dispatchMessage =
        typeof(UnreliableMessageSystem).GetMethod("OnEvent", PrivateInstance);
      Assert.IsNotNull(dispatchMessage);
      dispatchMessage.Invoke(messaging, new object[]
      {
        NetworkingEventCodes.TERRAIN_PREVIEW,
        "{\"cell\":{\"x\":1,\"y\":0,\"z\":0},\"shape\":1,\"direction\":0,\"tint\":{\"r\":1,\"g\":0,\"b\":0,\"a\":1},\"isHoverPreview\":false}",
        101
      });
      dispatchMessage.Invoke(messaging, new object[]
      {
        NetworkingEventCodes.TERRAIN_PREVIEW,
        "{\"cell\":{\"x\":2,\"y\":0,\"z\":0},\"shape\":1,\"direction\":0,\"tint\":{\"r\":0,\"g\":0,\"b\":1,\"a\":1},\"isHoverPreview\":false}",
        202
      });

      var networkedPreviews =
        (System.Collections.Generic.Dictionary<int, System.Collections.Generic.HashSet<GameObject>>)
        typeof(CreateTerrainPreview).GetField("networkedPreviews", PrivateInstance)
          .GetValue(preview);
      foreach (GameObject remotePreview in networkedPreviews[101])
      {
        redRemotePreview = remotePreview;
        break;
      }
      foreach (GameObject remotePreview in networkedPreviews[202])
      {
        blueRemotePreview = remotePreview;
        break;
      }
      Renderer redRenderer = redRemotePreview.GetComponentInChildren<Renderer>();
      Renderer blueRenderer = blueRemotePreview.GetComponentInChildren<Renderer>();
      MaterialPropertyBlock redProperties = new MaterialPropertyBlock();
      MaterialPropertyBlock blueProperties = new MaterialPropertyBlock();
      redRenderer.GetPropertyBlock(redProperties);
      blueRenderer.GetPropertyBlock(blueProperties);
      remoteTintsStayIndependent =
        redRenderer.sharedMaterial == previewMaterialInstance &&
        blueRenderer.sharedMaterial == previewMaterialInstance &&
        redProperties.GetColor(Shader.PropertyToID("_MainTint")) == Color.red &&
        blueProperties.GetColor(Shader.PropertyToID("_MainTint")) == Color.blue &&
        sourceMaterial.GetColor("_MainTint") == Color.white;

      dispatchMessage.Invoke(messaging, new object[]
      {
        NetworkingEventCodes.TERRAIN_PREVIEW,
        "CLEAR",
        101
      });
      clearOnlyRemovesSenderPreviews =
        networkedPreviews[101].Count == 0 &&
        networkedPreviews[202].Count == 1;

      Object.Destroy(previewOwner);
      yield return null;

      var registeredHandlers =
        (System.Collections.Generic.Dictionary<byte, System.Action<object, int>>)
        typeof(UnreliableMessageSystem).GetField("messageHandlers", PrivateInstance)
          .GetValue(messaging);
      handlerAndMaterialReleased =
        !registeredHandlers.ContainsKey(NetworkingEventCodes.TERRAIN_PREVIEW) &&
        previewMaterialInstance == null;
      previewsReleased =
        localPreview == null &&
        redRemotePreview == null &&
        blueRemotePreview == null;
      sharedMaterialPreserved = sourceMaterial != null;
    }
    finally
    {
      if (previewOwner != null) Object.DestroyImmediate(previewOwner);
      if (terrainOwner != null) Object.DestroyImmediate(terrainOwner);
      if (messagingOwner != null) Object.DestroyImmediate(messagingOwner);
      if (engineOwner != null) Object.DestroyImmediate(engineOwner);
      if (blockPrefab != null) Object.DestroyImmediate(blockPrefab);
      if (sourceMaterial != null) Object.DestroyImmediate(sourceMaterial);
      if (previewMaterialInstance != null) Object.DestroyImmediate(previewMaterialInstance);
      if (localPreview != null) Object.DestroyImmediate(localPreview);
      if (redRemotePreview != null) Object.DestroyImmediate(redRemotePreview);
      if (blueRemotePreview != null) Object.DestroyImmediate(blueRemotePreview);
    }

    yield return new ExitPlayMode();
    Assert.IsTrue(localTintUsesOwnedMaterial,
      "Local preview tint must modify only the component-owned material instance.");
    Assert.IsTrue(remoteTintsStayIndependent,
      "Remote previews must use independent property-block tint overrides without material clones.");
    Assert.IsTrue(clearOnlyRemovesSenderPreviews,
      "A remote CLEAR message must remove that sender's previews only.");
    Assert.IsTrue(handlerAndMaterialReleased,
      "Destroying the component must unregister its handler and release its preview material.");
    Assert.IsTrue(previewsReleased,
      "Destroying the component must release its local and remote preview objects.");
    Assert.IsTrue(sharedMaterialPreserved,
      "Preview teardown must leave the shared source material intact.");
  }

  [UnityTest]
  public IEnumerator DeleteAndEditTerrainPreviewsUsePropertyBlocksAndCleanGhosts()
  {
    yield return new EnterPlayMode();

    GameObject deleteOwner = null;
    GameObject editOwner = null;
    GameObject deleteTemplate = null;
    GameObject editTemplate = null;
    GameObject deleteGhost = null;
    GameObject editGhost = null;
    Material sourceMaterial = null;
    bool deleteTintUsesPropertyBlock = false;
    bool editTintUsesPropertyBlock = false;
    bool ghostsReleased = false;
    bool sourceMaterialPreserved = false;

    try
    {
      Shader shader = Shader.Find("Sully/PreviewMaterial");
      Assert.IsNotNull(shader);
      sourceMaterial = new Material(shader);
      sourceMaterial.SetColor("_MainTint", Color.green);
      Color sourceTint = sourceMaterial.GetColor("_MainTint");

      deleteTemplate = new GameObject("Delete terrain preview source");
      deleteTemplate.AddComponent<MeshRenderer>().sharedMaterial = sourceMaterial;
      editTemplate = new GameObject("Edit terrain preview source");
      editTemplate.AddComponent<MeshRenderer>().sharedMaterial = sourceMaterial;

      deleteOwner = new GameObject("Delete terrain preview owner");
      deleteOwner.SetActive(false);
      DeleteTerrainPreview deletePreview = deleteOwner.AddComponent<DeleteTerrainPreview>();
      typeof(DeleteTerrainPreview).GetField("previewObject", PrivateInstance)
        .SetValue(deletePreview, deleteTemplate);
      deleteOwner.SetActive(true);
      deletePreview.AddCellToPreview(new TerrainManager.Cell(1, 0, 0));

      var deletePreviews =
        (System.Collections.Generic.HashSet<GameObject>)typeof(DeleteTerrainPreview)
          .GetField("previewObjects", PrivateInstance).GetValue(deletePreview);
      foreach (GameObject previewObject in deletePreviews)
      {
        deleteGhost = previewObject;
        break;
      }
      Renderer deleteRenderer = deleteGhost.GetComponentInChildren<Renderer>();
      MaterialPropertyBlock deleteProperties = new MaterialPropertyBlock();
      deleteRenderer.GetPropertyBlock(deleteProperties);
      deleteTintUsesPropertyBlock =
        deleteRenderer.sharedMaterial == sourceMaterial &&
        deleteProperties.GetColor(Shader.PropertyToID("_MainTint")) == Color.red &&
        sourceMaterial.GetColor("_MainTint") == sourceTint;

      editOwner = new GameObject("Edit terrain preview owner");
      editOwner.SetActive(false);
      EditTerrainPreview editPreview = editOwner.AddComponent<EditTerrainPreview>();
      typeof(EditTerrainPreview).GetField("previewObject", PrivateInstance)
        .SetValue(editPreview, editTemplate);
      editOwner.SetActive(true);

      var candidates =
        new System.Collections.Generic.HashSet<Util.Tuple<TerrainManager.Cell, TerrainManager.CellValue>>
        {
          new Util.Tuple<TerrainManager.Cell, TerrainManager.CellValue>(
            new TerrainManager.Cell(2, 0, 0),
            new TerrainManager.CellValue(
              TerrainManager.BlockShape.Full,
              TerrainManager.BlockDirection.North,
              TerrainManager.BlockStyle.SolidColor0))
        };
      MethodInfo updateCells = typeof(EditTerrainPreview).GetMethod("UpdateCells", PrivateInstance);
      Assert.IsNotNull(updateCells);
      updateCells.Invoke(editPreview, new object[] { candidates });

      var editPreviews =
        (System.Collections.Generic.List<GameObject>)typeof(EditTerrainPreview)
          .GetField("previewObjects", PrivateInstance).GetValue(editPreview);
      editGhost = editPreviews[0];
      Renderer editRenderer = editGhost.GetComponentInChildren<Renderer>();
      MaterialPropertyBlock editProperties = new MaterialPropertyBlock();
      editRenderer.GetPropertyBlock(editProperties);
      editTintUsesPropertyBlock =
        editRenderer.sharedMaterial == sourceMaterial &&
        editProperties.GetColor(Shader.PropertyToID("_MainTint")) == Color.white &&
        sourceMaterial.GetColor("_MainTint") == sourceTint;

      Object.Destroy(deleteOwner);
      Object.Destroy(editOwner);
      yield return null;

      ghostsReleased = deleteGhost == null && editGhost == null;
      sourceMaterialPreserved =
        sourceMaterial != null && sourceMaterial.GetColor("_MainTint") == sourceTint;
    }
    finally
    {
      if (deleteOwner != null) Object.DestroyImmediate(deleteOwner);
      if (editOwner != null) Object.DestroyImmediate(editOwner);
      if (deleteTemplate != null) Object.DestroyImmediate(deleteTemplate);
      if (editTemplate != null) Object.DestroyImmediate(editTemplate);
      if (deleteGhost != null) Object.DestroyImmediate(deleteGhost);
      if (editGhost != null) Object.DestroyImmediate(editGhost);
      if (sourceMaterial != null) Object.DestroyImmediate(sourceMaterial);
    }

    yield return new ExitPlayMode();
    Assert.IsTrue(deleteTintUsesPropertyBlock,
      "Delete previews must tint their renderer without cloning or changing the source material.");
    Assert.IsTrue(editTintUsesPropertyBlock,
      "Edit previews must tint their renderer without cloning or changing the source material.");
    Assert.IsTrue(ghostsReleased,
      "Delete and edit preview owners must destroy their tracked ghosts during teardown.");
    Assert.IsTrue(sourceMaterialPreserved,
      "Destroying terrain preview ghosts must preserve their caller-owned source material.");
  }

  [Test]
  public void ActorTintAndRunStateUseIsolatedRendererPropertyBlocks()
  {
    Shader shader = Shader.Find("Sully/Snake");
    Assert.IsNotNull(shader);
    Material sourceMaterial = new Material(shader);
    sourceMaterial.SetColor("_MainTint", new Color(0.2f, 0.4f, 0.6f, 1f));
    sourceMaterial.SetInt("_Running", 1);
    Color sourceTint = sourceMaterial.GetColor("_MainTint");
    int tintProperty = Shader.PropertyToID("_MainTint");
    int runningProperty = Shader.PropertyToID("_Running");

    GameObject[] actorObjects = new GameObject[2];
    VoosActor[] actors = new VoosActor[2];
    MeshRenderer[] renderers = new MeshRenderer[2];
    Color[] actorTints = { Color.red, Color.blue };
    try
    {
      MethodInfo awake = typeof(VoosActor).GetMethod("Awake", PrivateInstance);
      FieldInfo networkingField = typeof(VoosActor).GetField("actorNetworking", PrivateInstance);
      FieldInfo currentRenderableField = typeof(VoosActor).GetField("currentRenderable", PrivateInstance);
      FieldInfo allRenderersField = typeof(VoosActor).GetField("allRenderers", PrivateInstance);
      FieldInfo tintableRenderersField = typeof(VoosActor).GetField("tintableRenderers", PrivateInstance);
      Assert.IsNotNull(awake);
      Assert.IsNotNull(networkingField);
      Assert.IsNotNull(currentRenderableField);
      Assert.IsNotNull(allRenderersField);
      Assert.IsNotNull(tintableRenderersField);

      for (int i = 0; i < actors.Length; i++)
      {
        actorObjects[i] = new GameObject("Actor renderer property block owner " + i);
        actorObjects[i].SetActive(false);
        ActorNetworking networking = actorObjects[i].AddComponent<ActorNetworking>();
        actors[i] = actorObjects[i].AddComponent<VoosActor>();

        GameObject renderable = new GameObject("Actor renderable " + i);
        renderable.transform.SetParent(actorObjects[i].transform, false);
        renderers[i] = renderable.AddComponent<MeshRenderer>();
        renderers[i].sharedMaterial = sourceMaterial;

        networkingField.SetValue(actors[i], networking);
        currentRenderableField.SetValue(actors[i], renderable);
        allRenderersField.SetValue(actors[i], new Renderer[] { renderers[i] });
        tintableRenderersField.SetValue(actors[i], new Renderer[] { renderers[i] });
        awake.Invoke(actors[i], null);

        actors[i].SetTint(actorTints[i]);
        actors[i].SetRunning(i != 0);
      }

      MaterialPropertyBlock firstProperties = new MaterialPropertyBlock();
      MaterialPropertyBlock secondProperties = new MaterialPropertyBlock();
      renderers[0].GetPropertyBlock(firstProperties);
      renderers[1].GetPropertyBlock(secondProperties);

      Assert.AreSame(sourceMaterial, renderers[0].sharedMaterial);
      Assert.AreSame(sourceMaterial, renderers[1].sharedMaterial);
      Assert.AreEqual(Color.red, firstProperties.GetColor(tintProperty));
      Assert.AreEqual(Color.blue, secondProperties.GetColor(tintProperty));
      Assert.AreEqual(0, firstProperties.GetInt(runningProperty));
      Assert.AreEqual(1, secondProperties.GetInt(runningProperty));
      Assert.AreEqual(sourceTint, sourceMaterial.GetColor("_MainTint"));
      Assert.AreEqual(1, sourceMaterial.GetInt("_Running"));
    }
    finally
    {
      for (int i = 0; i < actorObjects.Length; i++)
      {
        if (actorObjects[i] != null) Object.DestroyImmediate(actorObjects[i]);
      }
      if (sourceMaterial != null) Object.DestroyImmediate(sourceMaterial);
    }
  }

  [Test]
  public void UserBodyTintMaterialsAreIsolatedAndReleasedAcrossAvatarModes()
  {
    Shader shader = Shader.Find("Sully/Snake");
    Assert.IsNotNull(shader);
    Material sourceMaterial = null;
    Color sourceTint = Color.clear;

    GameObject[] owners = new GameObject[2];
    UserBody[] bodies = new UserBody[2];
    AvatarMaterialControl[] avatarControls = new AvatarMaterialControl[2];
    MeshRenderer[] avatarRenderers = new MeshRenderer[2];
    MeshRenderer[] permanentRenderers = new MeshRenderer[2];
    Material[][] avatarMaterials = { new Material[4], new Material[4] };
    Material[][] tintMaterialsToVerify = new Material[2][];
    bool tintMaterialsReleased = true;
    string[] avatarMaterialFields = {
      "avatarMaterialInstance",
      "avatarEyesMaterialInstance",
      "avatarHoloMaterialInstance",
      "avatarEyesHoloMaterialInstance"
    };
    string[] serializedRendererFields = {
      "avatarRenderers",
      "avatarPermanentRenderers",
      "hologramEffects",
      "avatarEyeRenderer",
      "avatarPermanentEyeRenderer"
    };
    FieldInfo tintRenderersField = typeof(UserBody).GetField("renderersForTint", PrivateInstance);
    FieldInfo avatarControlField = typeof(UserBody).GetField("avatarMaterialControl", PrivateInstance);
    FieldInfo[] avatarMaterialInstanceFields = new FieldInfo[avatarMaterialFields.Length];
    FieldInfo[] avatarRendererFields = new FieldInfo[serializedRendererFields.Length];
    MethodInfo userBodyOnDestroy = typeof(UserBody).GetMethod("OnDestroy", PrivateInstance);
    MethodInfo avatarOnDestroy = typeof(AvatarMaterialControl).GetMethod("OnDestroy", PrivateInstance);
    Assert.IsNotNull(tintRenderersField);
    Assert.IsNotNull(avatarControlField);
    Assert.IsNotNull(userBodyOnDestroy);
    Assert.IsNotNull(avatarOnDestroy);
    for (int i = 0; i < avatarMaterialFields.Length; i++)
    {
      avatarMaterialInstanceFields[i] =
        typeof(AvatarMaterialControl).GetField(avatarMaterialFields[i], PrivateInstance);
      Assert.IsNotNull(avatarMaterialInstanceFields[i]);
    }
    for (int i = 0; i < serializedRendererFields.Length; i++)
    {
      avatarRendererFields[i] =
        typeof(AvatarMaterialControl).GetField(serializedRendererFields[i], PrivateInstance);
      Assert.IsNotNull(avatarRendererFields[i]);
    }
    FieldInfo effectMaterialsField = typeof(AvatarMaterialControl).GetField(
      "hologramEffectMaterialInstances", PrivateInstance);
    Assert.IsNotNull(effectMaterialsField);

    Color[] tints = { Color.red, Color.blue };
    sourceMaterial = new Material(shader);
    sourceMaterial.SetColor("_MainTint", Color.gray);
    sourceTint = sourceMaterial.GetColor("_MainTint");
    try
    {
      for (int i = 0; i < owners.Length; i++)
      {
        owners[i] = new GameObject("UserBody tint material owner " + i);
        owners[i].SetActive(false);
        avatarControls[i] = owners[i].AddComponent<AvatarMaterialControl>();
        bodies[i] = owners[i].AddComponent<UserBody>();

        GameObject avatarObject = new GameObject("Tinted avatar renderer " + i);
        avatarObject.transform.SetParent(owners[i].transform, false);
        avatarRenderers[i] = avatarObject.AddComponent<MeshRenderer>();
        avatarRenderers[i].sharedMaterial = sourceMaterial;
        GameObject permanentObject = new GameObject("Permanent avatar renderer " + i);
        permanentObject.transform.SetParent(owners[i].transform, false);
        permanentRenderers[i] = permanentObject.AddComponent<MeshRenderer>();
        permanentRenderers[i].sharedMaterial = sourceMaterial;
        GameObject eyesObject = new GameObject("Avatar eyes renderer " + i);
        eyesObject.transform.SetParent(owners[i].transform, false);
        MeshRenderer eyesRenderer = eyesObject.AddComponent<MeshRenderer>();
        eyesRenderer.sharedMaterial = sourceMaterial;

        tintRenderersField.SetValue(bodies[i],
          new[] { avatarRenderers[i], permanentRenderers[i] });
        avatarControlField.SetValue(bodies[i], avatarControls[i]);
        avatarRendererFields[0].SetValue(avatarControls[i], new[] { avatarRenderers[i] });
        avatarRendererFields[1].SetValue(avatarControls[i], new[] { permanentRenderers[i] });
        avatarRendererFields[2].SetValue(avatarControls[i], new MeshRenderer[0]);
        avatarRendererFields[3].SetValue(avatarControls[i], eyesRenderer);
        avatarRendererFields[4].SetValue(avatarControls[i], eyesRenderer);
        effectMaterialsField.SetValue(avatarControls[i], new Material[0]);
        for (int j = 0; j < avatarMaterialFields.Length; j++)
        {
          avatarMaterials[i][j] = new Material(shader);
          avatarMaterialInstanceFields[j].SetValue(avatarControls[i], avatarMaterials[i][j]);
        }

        bodies[i].SetTint(tints[i]);
        Assert.AreSame(avatarMaterials[i][0], avatarRenderers[i].sharedMaterial,
          "AvatarMaterialControl must retain its standard material assignment.");
        Assert.AreEqual(tints[i], avatarRenderers[i].sharedMaterial.GetColor("_MainTint"));
        Assert.AreNotSame(sourceMaterial, permanentRenderers[i].sharedMaterial);
        Assert.AreEqual(tints[i], permanentRenderers[i].sharedMaterial.GetColor("_MainTint"));
      }

      avatarControls[0].SetToHologramMaterial();
      Color hologramTint = ArtUtil.GetHologramColor(tints[0]);
      Assert.AreSame(avatarMaterials[0][2], avatarRenderers[0].sharedMaterial);
      Assert.AreSame(avatarMaterials[0][2], permanentRenderers[0].sharedMaterial);
      Color assignedHologramTint = avatarRenderers[0].sharedMaterial.GetColor("_MainTint");
      Assert.That(assignedHologramTint.r, Is.EqualTo(hologramTint.r).Within(0.0001f));
      Assert.That(assignedHologramTint.g, Is.EqualTo(hologramTint.g).Within(0.0001f));
      Assert.That(assignedHologramTint.b, Is.EqualTo(hologramTint.b).Within(0.0001f));
      Assert.That(assignedHologramTint.a, Is.EqualTo(hologramTint.a).Within(0.0001f));

      avatarControls[0].SetToStandardMaterial();
      Assert.AreSame(avatarMaterials[0][0], avatarRenderers[0].sharedMaterial);
      bodies[0].SetTint(Color.green);
      Assert.AreSame(avatarMaterials[0][0], avatarRenderers[0].sharedMaterial);
      Assert.AreEqual(Color.green, avatarRenderers[0].sharedMaterial.GetColor("_MainTint"));
      Assert.AreEqual(Color.green, permanentRenderers[0].sharedMaterial.GetColor("_MainTint"));
      Assert.AreEqual(sourceTint, sourceMaterial.GetColor("_MainTint"));
      Assert.AreNotSame(permanentRenderers[0].sharedMaterial, permanentRenderers[1].sharedMaterial);

      FieldInfo ownedTintMaterialsField = typeof(UserBody).GetField(
        "ownedTintMaterials", PrivateInstance);
      Assert.IsNotNull(ownedTintMaterialsField);
      for (int i = 0; i < bodies.Length; i++)
      {
        System.Collections.IList ownedMaterials =
          (System.Collections.IList)ownedTintMaterialsField.GetValue(bodies[i]);
        tintMaterialsToVerify[i] = new Material[ownedMaterials.Count];
        ownedMaterials.CopyTo(tintMaterialsToVerify[i], 0);
      }
    }
    finally
    {
      for (int i = 0; i < owners.Length; i++)
      {
        if (bodies[i] != null) userBodyOnDestroy.Invoke(bodies[i], null);
        if (avatarControls[i] != null) avatarOnDestroy.Invoke(avatarControls[i], null);
        if (owners[i] != null) Object.DestroyImmediate(owners[i]);
        foreach (Material material in avatarMaterials[i])
        {
          if (material != null) Object.DestroyImmediate(material);
        }
      }
      foreach (Material[] materials in tintMaterialsToVerify)
      {
        if (materials == null) continue;
        foreach (Material material in materials)
        {
          if (material != null) tintMaterialsReleased = false;
        }
      }
      if (sourceMaterial != null) Object.DestroyImmediate(sourceMaterial);
    }
    Assert.IsTrue(tintMaterialsReleased,
      "UserBody must destroy every per-renderer tint material when its owner is torn down.");
  }

  [Test]
  public void CachedImageMaterialsOutliveMasterUntilLastCloneIsReleased()
  {
    System.Type assetCacheType = typeof(GameDetail).Assembly.GetType("AssetCache");
    Assert.IsNotNull(assetCacheType);
    MethodInfo createImageResources = assetCacheType.GetMethod(
      "CreateImageMaterialResources", BindingFlags.Static | BindingFlags.NonPublic);
    MethodInfo releaseAssetClone = assetCacheType.GetMethod(
      "ReleaseAssetClone", BindingFlags.Static | BindingFlags.Public);
    Assert.IsNotNull(createImageResources);
    Assert.IsNotNull(releaseAssetClone);

    System.Type resourceSetType = typeof(GameDetail).Assembly.GetType("RuntimeModelResourceSet");
    Assert.IsNotNull(resourceSetType);
    MethodInfo cloneResourceSet = resourceSetType.GetMethod(
      "Clone", BindingFlags.Instance | BindingFlags.Public);
    MethodInfo releaseOwner = resourceSetType.GetMethod(
      "ReleaseOwner", BindingFlags.Instance | BindingFlags.Public);
    Assert.IsNotNull(cloneResourceSet);
    Assert.IsNotNull(releaseOwner);

    Shader shader = Shader.Find("Standard");
    Assert.IsNotNull(shader);
    GameObject master = null;
    Material sourceMaterial = null;
    Material cachedMaterial = null;
    Texture2D sourceTexture = null;
    Texture2D imageTexture = null;
    object resources = null;
    GameObject firstClone = null;
    GameObject secondClone = null;
    try
    {
      master = new GameObject("Cached image master");
      master.SetActive(false);
      sourceTexture = new Texture2D(1, 1);
      imageTexture = new Texture2D(1, 1);
      sourceMaterial = new Material(shader);
      sourceMaterial.mainTexture = sourceTexture;
      MeshRenderer renderer = master.AddComponent<MeshRenderer>();
      renderer.sharedMaterial = sourceMaterial;

      resources = createImageResources.Invoke(null, new object[] { renderer, imageTexture });
      cachedMaterial = renderer.sharedMaterial;
      Assert.AreNotSame(sourceMaterial, cachedMaterial,
        "Each cached image needs its own material so its URL texture cannot mutate the prefab material.");
      Assert.AreSame(imageTexture, cachedMaterial.mainTexture);
      Assert.AreSame(sourceTexture, sourceMaterial.mainTexture);

      firstClone = (GameObject)cloneResourceSet.Invoke(resources, new object[] { master });
      secondClone = (GameObject)cloneResourceSet.Invoke(resources, new object[] { firstClone });
      Assert.AreSame(cachedMaterial, secondClone.GetComponent<Renderer>().sharedMaterial);

      releaseOwner.Invoke(resources, null);
      resources = null;
      Object.DestroyImmediate(master);
      master = null;
      Assert.IsTrue(cachedMaterial != null && imageTexture != null,
        "Clones must retain the cached image material and texture after the master is disposed.");

      releaseAssetClone.Invoke(null, new object[] { firstClone });
      firstClone = null;
      Assert.IsTrue(cachedMaterial != null && imageTexture != null,
        "The final clone must keep image resources alive after earlier clones are released.");

      releaseAssetClone.Invoke(null, new object[] { secondClone });
      secondClone = null;
      Assert.IsTrue(cachedMaterial == null && imageTexture == null,
        "The cached image material and texture must be destroyed after the final clone is released.");
      Assert.IsTrue(sourceMaterial != null && sourceTexture != null);
      Assert.AreSame(sourceTexture, sourceMaterial.mainTexture);
    }
    finally
    {
      if (firstClone != null) releaseAssetClone.Invoke(null, new object[] { firstClone });
      if (secondClone != null) releaseAssetClone.Invoke(null, new object[] { secondClone });
      if (resources != null) releaseOwner.Invoke(resources, null);
      if (master != null) Object.DestroyImmediate(master);
      if (cachedMaterial != null) Object.DestroyImmediate(cachedMaterial);
      if (sourceMaterial != null) Object.DestroyImmediate(sourceMaterial);
      if (sourceTexture != null) Object.DestroyImmediate(sourceTexture);
      if (imageTexture != null) Object.DestroyImmediate(imageTexture);
    }
  }

  [Test]
  public void ActorPhysicsMaterialsAreIsolatedAndReleased()
  {
    MethodInfo releaseMaterials = typeof(VoosActor).GetMethod(
      "ReleaseOwnedPhysicsMaterials", PrivateInstance);
    FieldInfo renderableCollidersField = typeof(VoosActor).GetField(
      "renderableColliders", PrivateInstance);
    Assert.IsNotNull(releaseMaterials);
    Assert.IsNotNull(renderableCollidersField);

    PhysicsMaterial sourceMaterial = null;
    GameObject[] owners = new GameObject[2];
    VoosActor[] actors = new VoosActor[2];
    BoxCollider[] colliders = new BoxCollider[2];
    PhysicsMaterial[] ownedMaterials = new PhysicsMaterial[2];
    PhysicsMaterial[] replacementMaterials = new PhysicsMaterial[2];
    try
    {
      sourceMaterial = new PhysicsMaterial();
      sourceMaterial.name = "Actor source physics material";
      sourceMaterial.bounciness = 0.12f;
      sourceMaterial.dynamicFriction = 0.37f;
      sourceMaterial.staticFriction = 0.64f;
      sourceMaterial.bounceCombine = PhysicsMaterialCombine.Maximum;

      for (int i = 0; i < actors.Length; i++)
      {
        owners[i] = new GameObject("Actor physics material owner " + i);
        owners[i].SetActive(false);
        actors[i] = owners[i].AddComponent<VoosActor>();
        GameObject colliderObject = new GameObject("Actor collider " + i);
        colliderObject.transform.SetParent(owners[i].transform, false);
        colliders[i] = colliderObject.AddComponent<BoxCollider>();
        colliders[i].sharedMaterial = sourceMaterial;
        renderableCollidersField.SetValue(actors[i], new Collider[] { colliders[i] });

        actors[i].SetBounciness(i == 0 ? 0.35f : 0.8f);
        ownedMaterials[i] = colliders[i].sharedMaterial;
        Assert.AreNotSame(sourceMaterial, ownedMaterials[i]);
        Assert.That(ownedMaterials[i].bounciness,
          Is.EqualTo(i == 0 ? 0.35f : 0.8f).Within(0.0001f));
        Assert.AreEqual(sourceMaterial.dynamicFriction, ownedMaterials[i].dynamicFriction);
        Assert.AreEqual(sourceMaterial.staticFriction, ownedMaterials[i].staticFriction);
        Assert.AreEqual(sourceMaterial.bounceCombine, ownedMaterials[i].bounceCombine);
      }

      Assert.AreNotSame(ownedMaterials[0], ownedMaterials[1],
        "Each actor must own its collider material rather than mutating the shared source.");
      actors[0].SetBounciness(0.45f);
      Assert.AreSame(ownedMaterials[0], colliders[0].sharedMaterial,
        "Changing bounciness must reuse the actor's existing material copy.");
      Assert.That(ownedMaterials[0].bounciness, Is.EqualTo(0.45f).Within(0.0001f));
      Assert.That(sourceMaterial.bounciness, Is.EqualTo(0.12f).Within(0.0001f));

      for (int i = 0; i < actors.Length; i++)
      {
        releaseMaterials.Invoke(actors[i], null);
        Assert.IsTrue(ownedMaterials[i] == null,
          "Replacing an actor renderable must destroy its previous collider material copies.");
        Assert.IsNotNull(sourceMaterial,
          "Releasing actor-owned copies must preserve the shared source material.");

        colliders[i].sharedMaterial = sourceMaterial;
        actors[i].UpdatePhysicsMaterial();
        replacementMaterials[i] = colliders[i].sharedMaterial;
        Assert.AreNotSame(sourceMaterial, replacementMaterials[i]);
      }

      for (int i = 0; i < actors.Length; i++)
      {
        releaseMaterials.Invoke(actors[i], null);
        Assert.IsTrue(replacementMaterials[i] == null,
          "Actor teardown must destroy every currently owned collider material copy.");
      }
      Assert.IsTrue(sourceMaterial != null);
      Assert.That(sourceMaterial.dynamicFriction, Is.EqualTo(0.37f));
      Assert.That(sourceMaterial.staticFriction, Is.EqualTo(0.64f));
    }
    finally
    {
      for (int i = 0; i < actors.Length; i++)
      {
        if (actors[i] != null) releaseMaterials.Invoke(actors[i], null);
        if (owners[i] != null) Object.DestroyImmediate(owners[i]);
      }
      if (sourceMaterial != null) Object.DestroyImmediate(sourceMaterial);
    }
  }

  [Test]
  public void GameUiMainReleasesItsDraw2DMaterialOnTeardown()
  {
    TextAsset fontConfig = null;
    Texture2D fontAtlas = null;
    GameObject owner = null;
    Draw2DUnityImpl draw2D = null;
    Material ownedMaterial = null;
    try
    {
      fontConfig = new TextAsset(
        "<font><info size=\"16\"/><common scaleW=\"1\" scaleH=\"1\"/>" +
        "<pages><page id=\"0\"/></pages><chars /></font>");
      fontAtlas = new Texture2D(1, 1);
      draw2D = new Draw2DUnityImpl(fontConfig, fontAtlas);
      FieldInfo materialField = typeof(Draw2DUnityImpl).GetField(
        "material", PrivateInstance);
      FieldInfo draw2DField = typeof(GameUiMain).GetField(
        "draw2D", PrivateInstance);
      MethodInfo onDestroy = typeof(GameUiMain).GetMethod(
        "OnDestroy", PrivateInstance);
      Assert.IsNotNull(materialField);
      Assert.IsNotNull(draw2DField);
      Assert.IsNotNull(onDestroy);
      ownedMaterial = (Material)materialField.GetValue(draw2D);
      Assert.IsNotNull(ownedMaterial);

      owner = new GameObject("Game UI Draw2D material owner");
      owner.SetActive(false);
      GameUiMain gameUiMain = owner.AddComponent<GameUiMain>();
      draw2DField.SetValue(gameUiMain, draw2D);
      onDestroy.Invoke(gameUiMain, null);
      draw2D = null;

      Assert.IsTrue(ownedMaterial == null,
        "GameUiMain teardown must destroy the Draw2D renderer's owned material.");
      Assert.IsNull(draw2DField.GetValue(gameUiMain));
    }
    finally
    {
      if (draw2D != null) draw2D.Dispose();
      if (owner != null) Object.DestroyImmediate(owner);
      if (fontConfig != null) Object.DestroyImmediate(fontConfig);
      if (fontAtlas != null) Object.DestroyImmediate(fontAtlas);
    }
  }

  [Test]
  public void InvalidDraw2DFontDoesNotLeaveAnOwnedMaterial()
  {
    TextAsset fontConfig = null;
    Texture2D fontAtlas = null;
    Material[] materialsBefore = null;
    try
    {
      fontConfig = new TextAsset("<font />");
      fontAtlas = new Texture2D(1, 1);
      materialsBefore = Resources.FindObjectsOfTypeAll<Material>();
      Assert.Throws<System.InvalidOperationException>(
        () => new Draw2DUnityImpl(fontConfig, fontAtlas));

      bool leakedMaterialFound = false;
      foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
      {
        if (material == null) continue;
        bool existedBeforeConstructor = false;
        foreach (Material existing in materialsBefore)
        {
          if (object.ReferenceEquals(existing, material))
          {
            existedBeforeConstructor = true;
            break;
          }
        }
        if (existedBeforeConstructor) continue;
        leakedMaterialFound = true;
        Object.DestroyImmediate(material);
      }
      Assert.IsFalse(leakedMaterialFound,
        "Invalid font data must fail before allocating the Draw2D material.");
    }
    finally
    {
      if (fontConfig != null) Object.DestroyImmediate(fontConfig);
      if (fontAtlas != null) Object.DestroyImmediate(fontAtlas);
    }
  }

}
#endif
