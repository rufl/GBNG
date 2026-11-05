#if UNITY_EDITOR && USE_STEAMWORKS
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using GameBuilder.Workshop;
using NUnit.Framework;
using Steamworks;
using UnityEngine;
using Object = UnityEngine.Object;

public class SteamWorkshopQuerySettingsTests
{
  GameObject serviceObject;
  SteamWorkshopService service;

  [SetUp]
  public void SetUp()
  {
    service = SteamWorkshopService.Instance;
    if (service == null)
    {
      serviceObject = new GameObject("Workshop query settings test service");
      service = serviceObject.AddComponent<SteamWorkshopService>();
    }
  }

  [TearDown]
  public void TearDown()
  {
    if (serviceObject != null) Object.DestroyImmediate(serviceObject);
  }

  [Test]
  public void ResetItemSearchRestoresLegacyAnyTagSearchDefaults()
  {
    service.SearchText = "old query";
    service.SearchMatchAnyTag = false;
    service.SearchTags.Add("racing");
    service.ExcludedTags.Add("broken");
    service.Sorting = new WorkshopSortMode(EUGCQuery.k_EUGCQuery_RankedByVote);

    service.ResetItemSearch();

    Assert.That(service.SearchText, Is.Empty);
    Assert.That(service.SearchMatchAnyTag, Is.True);
    Assert.That(service.SearchTags, Is.Empty);
    Assert.That(service.ExcludedTags, Is.Empty);
    Assert.That(service.Sorting.Query, Is.EqualTo(EUGCQuery.k_EUGCQuery_RankedByTrend));
  }

  [Test]
  public void FavoriteQueryFlagsOnlyItemsInTheSteamFavoriteSet()
  {
    var first = new WorkshopItem { PublishedFileId = 101, IsFavorited = true };
    var second = new WorkshopItem { PublishedFileId = 202 };
    var third = new WorkshopItem { PublishedFileId = 303, IsFavorited = true };

    ApplyFavoriteFlags(new List<WorkshopItem> { first, second, third }, new HashSet<ulong> { 202 });

    Assert.That(first.IsFavorited, Is.False);
    Assert.That(second.IsFavorited, Is.True);
    Assert.That(third.IsFavorited, Is.False);
  }

  [Test]
  public void FavoriteChangeUpdatesTheItemForBothAddAndRemove()
  {
    var item = new WorkshopItem();

    ApplyFavoriteChange(item, true);
    Assert.That(item.IsFavorited, Is.True);

    ApplyFavoriteChange(item, false);
    Assert.That(item.IsFavorited, Is.False);
  }

  [Test]
  public void UploadProgressUsesLegacyWeightedStagesAndKeepsCompletedProgress()
  {
    Assert.That(MapUploadProgress(EItemUpdateStatus.k_EItemUpdateStatusInvalid, 0, 0, EItemUpdateStatus.k_EItemUpdateStatusInvalid), Is.Zero);
    Assert.That(MapUploadProgress(EItemUpdateStatus.k_EItemUpdateStatusPreparingConfig, 100, 100, EItemUpdateStatus.k_EItemUpdateStatusPreparingConfig), Is.Zero);
    Assert.That(MapUploadProgress(EItemUpdateStatus.k_EItemUpdateStatusPreparingContent, 50, 100, EItemUpdateStatus.k_EItemUpdateStatusPreparingContent), Is.EqualTo(0.05f).Within(0.0001f));
    Assert.That(MapUploadProgress(EItemUpdateStatus.k_EItemUpdateStatusUploadingContent, 50, 100, EItemUpdateStatus.k_EItemUpdateStatusUploadingContent), Is.EqualTo(0.425f).Within(0.0001f));
    Assert.That(MapUploadProgress(EItemUpdateStatus.k_EItemUpdateStatusUploadingPreviewFile, 50, 100, EItemUpdateStatus.k_EItemUpdateStatusUploadingPreviewFile), Is.EqualTo(0.825f).Within(0.0001f));
    Assert.That(MapUploadProgress(EItemUpdateStatus.k_EItemUpdateStatusCommittingChanges, 50, 100, EItemUpdateStatus.k_EItemUpdateStatusCommittingChanges), Is.EqualTo(0.95f).Within(0.0001f));
    Assert.That(MapUploadProgress(EItemUpdateStatus.k_EItemUpdateStatusInvalid, 0, 0, EItemUpdateStatus.k_EItemUpdateStatusUploadingContent), Is.EqualTo(1));
  }

  [Test]
  public void WorkshopItemIsReadyRequiresInstalledCurrentContent()
  {
    var item = new WorkshopItem { IsInstalled = true };
    Assert.That(item.IsReady, Is.True);

    item.IsUpdateNeeded = true;
    Assert.That(item.IsReady, Is.False);

    item.IsUpdateNeeded = false;
    item.IsDownloading = true;
    Assert.That(item.IsReady, Is.False);

    item.IsDownloading = false;
    item.IsInstalled = false;
    Assert.That(item.IsReady, Is.False);
  }

  [Test]
  public void RenderIconResolutionIsCappedByCameraPixelRect()
  {
    Rect cameraRect = new Rect(0, 0, 320.5f, 200.8f);
    Assert.That(ClampIconSize(512, 480, cameraRect), Is.EqualTo(new Vector2Int(320, 200)));
    Assert.That(ClampIconSize(100, 80, cameraRect), Is.EqualTo(new Vector2Int(100, 80)));
  }

  [Test]
  public void RenderIconRestoresCameraStateAndWritesPng()
  {
    GameObject cameraObject = new GameObject("Workshop icon test camera");
    RenderTexture cameraTarget = new RenderTexture(32, 32, 24);
    Texture2D renderedTexture = null;
    Texture2D decodedTexture = null;
    string iconPath = Path.Combine(Path.GetTempPath(), "GameBuilderWorkshopIcon-" + Guid.NewGuid().ToString("N") + ".png");
    try
    {
      cameraTarget.Create();
      Camera camera = cameraObject.AddComponent<Camera>();
      camera.targetTexture = cameraTarget;
      camera.pixelRect = new Rect(0, 0, 32, 32);
      camera.clearFlags = CameraClearFlags.SolidColor;
      camera.backgroundColor = Color.green;
      Rect originalPixelRect = camera.pixelRect;
      RenderTexture originalActive = RenderTexture.active;
      bool callbackCalled = false;
      MethodInfo method = typeof(SteamWorkshopService).GetMethod("RenderIconRoutine", BindingFlags.NonPublic | BindingFlags.Instance);
      Assert.That(method, Is.Not.Null);

      var routine = (IEnumerator)method.Invoke(service, new object[]
      {
        camera, 32, 32, iconPath, false,
        new Action<Texture2D>(texture => { renderedTexture = texture; callbackCalled = true; })
      });
      Assert.That(routine.MoveNext(), Is.True);
      Assert.That(routine.MoveNext(), Is.False);

      Assert.That(callbackCalled, Is.True);
      Assert.That(renderedTexture, Is.Not.Null);
      Assert.That(renderedTexture.width, Is.EqualTo(32));
      Assert.That(renderedTexture.height, Is.EqualTo(32));
      Assert.That(camera.targetTexture, Is.SameAs(cameraTarget));
      Assert.That(camera.pixelRect, Is.EqualTo(originalPixelRect));
      Assert.That(RenderTexture.active, Is.SameAs(originalActive));
      Assert.That(File.Exists(iconPath), Is.True);
      decodedTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);
      Assert.That(decodedTexture.LoadImage(File.ReadAllBytes(iconPath)), Is.True);
      Assert.That(decodedTexture.GetPixel(16, 16).g, Is.GreaterThan(0.8f));
    }
    finally
    {
      if (renderedTexture != null) Object.DestroyImmediate(renderedTexture);
      if (decodedTexture != null) Object.DestroyImmediate(decodedTexture);
      Object.DestroyImmediate(cameraObject);
      Object.DestroyImmediate(cameraTarget);
      if (File.Exists(iconPath)) File.Delete(iconPath);
    }
  }

  static void ApplyFavoriteFlags(IEnumerable<WorkshopItem> items, HashSet<ulong> favoriteIds)
  {
    MethodInfo method = typeof(SteamWorkshopService).GetMethod("ApplyFavoriteFlags", BindingFlags.NonPublic | BindingFlags.Static);
    Assert.That(method, Is.Not.Null);
    method.Invoke(null, new object[] { items, favoriteIds });
  }

  static void ApplyFavoriteChange(WorkshopItem item, bool wasAddRequest)
  {
    MethodInfo method = typeof(SteamWorkshopService).GetMethod("ApplyFavoriteChange", BindingFlags.NonPublic | BindingFlags.Static);
    Assert.That(method, Is.Not.Null);
    method.Invoke(null, new object[] { item, wasAddRequest });
  }

  static Vector2Int ClampIconSize(int width, int height, Rect cameraRect)
  {
    MethodInfo method = typeof(SteamWorkshopService).GetMethod("ClampIconSize", BindingFlags.NonPublic | BindingFlags.Static);
    Assert.That(method, Is.Not.Null);
    return (Vector2Int)method.Invoke(null, new object[] { width, height, cameraRect });
  }
  static float MapUploadProgress(EItemUpdateStatus state, ulong processed, ulong total, EItemUpdateStatus lastValidState)
  {
    MethodInfo method = typeof(SteamWorkshopService).GetMethod("MapUploadProgress", BindingFlags.NonPublic | BindingFlags.Static);
    Assert.That(method, Is.Not.Null);
    return (float)method.Invoke(null, new object[] { state, processed, total, lastValidState });
  }
}
#endif
