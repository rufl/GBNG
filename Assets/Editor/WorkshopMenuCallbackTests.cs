#if UNITY_EDITOR && USE_STEAMWORKS
using System;
using System.Collections.Generic;
using System.Reflection;
using GameBuilder.Workshop;
using GameBuilder;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class WorkshopMenuCallbackTests
{
  [Test]
  public void LateWorkshopResultsAfterMenuDestructionAreIgnored()
  {
    GameObject menuObject = new GameObject("WorkshopMenuCallbackTests");
    WorkshopMenu menu = menuObject.AddComponent<WorkshopMenu>();
    FieldInfo pageCountField = typeof(WorkshopMenu).GetField(
      "pageCount", BindingFlags.Instance | BindingFlags.NonPublic);
    Assert.IsNotNull(pageCountField);
    uint pageCountBeforeDestroy = (uint)pageCountField.GetValue(menu);
    UnityEngine.Object.DestroyImmediate(menuObject);

    MethodInfo callback = typeof(WorkshopMenu).GetMethod(
      "OnWorkshopListLoaded", BindingFlags.Instance | BindingFlags.NonPublic);
    Type itemListDataType = typeof(WorkshopMenu).GetNestedType("ItemListData", BindingFlags.NonPublic);
    Assert.IsNotNull(callback);
    Assert.IsNotNull(itemListDataType);
    object itemListData = Activator.CreateInstance(itemListDataType);
    WorkshopItemListEventArgs args = new WorkshopItemListEventArgs(
      new WorkshopItemList(new List<WorkshopItem>(), 9));

    callback.Invoke(menu, new object[] { args, itemListData, 0 });
    Assert.AreEqual(pageCountBeforeDestroy, pageCountField.GetValue(menu));
  }

  [Test]
  public void SuccessfulEmptyResultToleratesMissingFeedbackObjects()
  {
    GameObject menuObject = new GameObject("WorkshopMenuCallbackTests Missing Feedback");
    menuObject.SetActive(false);
    WorkshopMenu menu = menuObject.AddComponent<WorkshopMenu>();
    MethodInfo callback = typeof(WorkshopMenu).GetMethod(
      "OnWorkshopListLoaded", BindingFlags.Instance | BindingFlags.NonPublic);
    Type itemListDataType = typeof(WorkshopMenu).GetNestedType("ItemListData", BindingFlags.NonPublic);
    FieldInfo pageCountField = typeof(WorkshopMenu).GetField(
      "pageCount", BindingFlags.Instance | BindingFlags.NonPublic);
    WorkshopItemListEventArgs args = new WorkshopItemListEventArgs(
      new WorkshopItemList(new List<WorkshopItem>(), 7));

    try
    {
      Assert.IsNotNull(callback);
      Assert.IsNotNull(itemListDataType);
      Assert.IsNotNull(pageCountField);
      callback.Invoke(menu, new object[] { args, Activator.CreateInstance(itemListDataType), 0 });
      Assert.AreEqual(7u, pageCountField.GetValue(menu));
    }
    finally
    {
      UnityEngine.Object.DestroyImmediate(menuObject);
    }
  }

  [Test]
  public void WorkshopErrorCallbackStopsLoadingIndicator()
  {
    GameObject menuObject = new GameObject("WorkshopMenuCallbackTests Error");
    menuObject.SetActive(false);
    WorkshopMenu menu = menuObject.AddComponent<WorkshopMenu>();
    GameObject loadingFeedback = new GameObject("Workshop loading feedback");
    FieldInfo loadingFeedbackField = typeof(ThumbnailMenu).GetField(
      "workingFeedback", BindingFlags.Instance | BindingFlags.NonPublic);
    MethodInfo callback = typeof(WorkshopMenu).GetMethod(
      "OnWorkshopListLoaded", BindingFlags.Instance | BindingFlags.NonPublic);
    Type itemListDataType = typeof(WorkshopMenu).GetNestedType("ItemListData", BindingFlags.NonPublic);
    WorkshopItemListEventArgs args = new WorkshopItemListEventArgs(
      new WorkshopItemList(new List<WorkshopItem>(), 0), true, "offline");

    try
    {
      Assert.IsNotNull(loadingFeedbackField);
      Assert.IsNotNull(callback);
      Assert.IsNotNull(itemListDataType);
      loadingFeedbackField.SetValue(menu, loadingFeedback);
      callback.Invoke(menu, new object[] { args, Activator.CreateInstance(itemListDataType), 0 });
      Assert.IsFalse(loadingFeedback.activeSelf);
    }
    finally
    {
      UnityEngine.Object.DestroyImmediate(menuObject);
      UnityEngine.Object.DestroyImmediate(loadingFeedback);
    }
  }

  [Test]
  public void WorkshopUnavailableWithoutSteamStopsLoadingIndicator()
  {
    GameObject menuObject = new GameObject("WorkshopMenuCallbackTests Offline");
    menuObject.SetActive(false);
    WorkshopMenu menu = menuObject.AddComponent<WorkshopMenu>();
    GameObject loadingFeedback = new GameObject("Workshop loading feedback");
    FieldInfo loadingFeedbackField = typeof(ThumbnailMenu).GetField(
      "workingFeedback", BindingFlags.Instance | BindingFlags.NonPublic);
    PropertyInfo initializedProperty = typeof(SteamRuntime).GetProperty(
      "Initialized", BindingFlags.Static | BindingFlags.Public);
    Assert.IsNotNull(loadingFeedbackField);
    Assert.IsNotNull(initializedProperty);
    loadingFeedbackField.SetValue(menu, loadingFeedback);
    MethodInfo initializedSetter = initializedProperty.GetSetMethod(true);
    bool wasInitialized = SteamRuntime.Initialized;
    MethodInfo createThumbnails = typeof(WorkshopMenu).GetMethod(
      "CreateSteamThumbnails", BindingFlags.Instance | BindingFlags.NonPublic);

    try
    {
      Assert.IsNotNull(initializedSetter);
      Assert.IsNotNull(createThumbnails);
      initializedSetter.Invoke(null, new object[] { false });
      createThumbnails.Invoke(menu, null);
      Assert.IsFalse(loadingFeedback.activeSelf);
    }
    finally
    {
      initializedSetter.Invoke(null, new object[] { wasInitialized });
      UnityEngine.Object.DestroyImmediate(menuObject);
      UnityEngine.Object.DestroyImmediate(loadingFeedback);
    }
  }

  [Test]
  public void ClosingWorkshopMenuInvalidatesInFlightResults()
  {
    GameObject menuObject = new GameObject("WorkshopMenuCallbackTests Closed Menu");
    WorkshopMenu menu = menuObject.AddComponent<WorkshopMenu>();
    FieldInfo requestIdField = typeof(WorkshopMenu).GetField(
      "itemListRequestId", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo pageCountField = typeof(WorkshopMenu).GetField(
      "pageCount", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo gettingItemListField = typeof(WorkshopMenu).GetField(
      "gettingItemList", BindingFlags.Instance | BindingFlags.NonPublic);
    MethodInfo callback = typeof(WorkshopMenu).GetMethod(
      "OnWorkshopListLoaded", BindingFlags.Instance | BindingFlags.NonPublic);
    Type itemListDataType = typeof(WorkshopMenu).GetNestedType("ItemListData", BindingFlags.NonPublic);
    WorkshopItemListEventArgs args = new WorkshopItemListEventArgs(
      new WorkshopItemList(new List<WorkshopItem>(), 9));

    try
    {
      Assert.IsNotNull(requestIdField);
      Assert.IsNotNull(pageCountField);
      Assert.IsNotNull(gettingItemListField);
      Assert.IsNotNull(callback);
      Assert.IsNotNull(itemListDataType);
      requestIdField.SetValue(menu, 12);
      gettingItemListField.SetValue(menu, true);

      menu.Close();
      callback.Invoke(menu, new object[] { args, Activator.CreateInstance(itemListDataType), 12 });

      Assert.AreEqual(1u, pageCountField.GetValue(menu));
      Assert.IsFalse((bool)gettingItemListField.GetValue(menu));
    }
    finally
    {
      UnityEngine.Object.DestroyImmediate(menuObject);
    }
  }

  [Test]
  public void ChangingSearchOrSortResetsWorkshopPagination()
  {
    GameObject menuObject = new GameObject("WorkshopMenuCallbackTests Pagination");
    menuObject.SetActive(false);
    WorkshopMenu menu = menuObject.AddComponent<WorkshopMenu>();
    GameObject sortLabelObject = new GameObject(
      "Workshop sort label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TMPro.TextMeshProUGUI));
    TMPro.TextMeshProUGUI sortLabel = sortLabelObject.GetComponent<TMPro.TextMeshProUGUI>();
    FieldInfo sortTextField = typeof(ThumbnailMenu).GetField(
      "sortText", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo currentSearchField = typeof(ThumbnailMenu).GetField(
      "currentSearchString", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo curPageField = typeof(WorkshopMenu).GetField(
      "curPage", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo pageCountField = typeof(WorkshopMenu).GetField(
      "pageCount", BindingFlags.Instance | BindingFlags.NonPublic);
    MethodInfo onSearch = typeof(WorkshopMenu).GetMethod(
      "OnSearch", BindingFlags.Instance | BindingFlags.NonPublic);
    PropertyInfo initializedProperty = typeof(SteamRuntime).GetProperty(
      "Initialized", BindingFlags.Static | BindingFlags.Public);
    Assert.IsNotNull(initializedProperty);
    MethodInfo initializedSetter = initializedProperty.GetSetMethod(true);
    bool wasInitialized = SteamRuntime.Initialized;

    try
    {
      Assert.IsNotNull(sortTextField);
      Assert.IsNotNull(currentSearchField);
      Assert.IsNotNull(curPageField);
      Assert.IsNotNull(pageCountField);
      Assert.IsNotNull(onSearch);
      Assert.IsNotNull(initializedSetter);
      sortTextField.SetValue(menu, sortLabel);
      currentSearchField.SetValue(menu, "old");
      initializedSetter.Invoke(null, new object[] { false });

      curPageField.SetValue(menu, 4u);
      pageCountField.SetValue(menu, 8u);
      LogAssert.Expect(LogType.Log, "Steam Workshop is not available right now. Did you launch from Steam?");
      onSearch.Invoke(menu, new object[] { "new" });
      Assert.AreEqual(1u, curPageField.GetValue(menu));
      Assert.AreEqual(1u, pageCountField.GetValue(menu));

      curPageField.SetValue(menu, 3u);
      pageCountField.SetValue(menu, 6u);
      LogAssert.Expect(LogType.Log, "Steam Workshop is not available right now. Did you launch from Steam?");
      menu.SetSort(WorkshopMenu.WorkshopSort.Newest);
      Assert.AreEqual(1u, curPageField.GetValue(menu));
      Assert.AreEqual(1u, pageCountField.GetValue(menu));
    }
    finally
    {
      initializedSetter.Invoke(null, new object[] { wasInitialized });
      UnityEngine.Object.DestroyImmediate(menuObject);
      UnityEngine.Object.DestroyImmediate(sortLabelObject);
    }
  }

  [Test]
  public void WorkshopMenuRefreshesBrowseOnEveryOpen()
  {
    GameObject root = new GameObject("WorkshopMenuCallbackTests Refresh");
    root.SetActive(false);
    GameObject menuObject = CreateInactiveObject("Workshop menu", root.transform);
    WorkshopMenu menu = menuObject.AddComponent<WorkshopMenu>();
    GameObject dependencies = CreateInactiveObject("Dependencies", root.transform);
    GameBundleLibrary gameBundleLibrary = dependencies.AddComponent<GameBundleLibrary>();
    GameBuilderSceneController sceneController = dependencies.AddComponent<GameBuilderSceneController>();
    GameObject categoryContainer = CreateInactiveObject("Category container", root.transform);
    GameObject categoryPrefab = CreateInactiveObject("Category prefab", root.transform);
    ThumbnailCategory category = categoryPrefab.AddComponent<ThumbnailCategory>();
    TMPro.TextMeshProUGUI categoryName = CreateTestText("Category name", categoryPrefab.transform);
    TMPro.TextMeshProUGUI categoryPlaceholder = CreateTestText("Category placeholder", categoryPrefab.transform);
    SetField(category, typeof(ThumbnailCategory), "nameText", categoryName);
    SetField(category, typeof(ThumbnailCategory), "placeholderText", categoryPlaceholder);
    GameObject gameDetailPrefabObject = CreateInactiveObject("Game detail prefab", root.transform);
    GameDetail gameDetailPrefab = gameDetailPrefabObject.AddComponent<GameDetail>();
    SetField(menu, typeof(ThumbnailMenu), "gameBundleLibrary", gameBundleLibrary);
    SetField(menu, typeof(ThumbnailMenu), "sceneController", sceneController);
    SetField(menu, typeof(ThumbnailMenu), "sortLeft", CreateTestButton("Sort left", root.transform));
    SetField(menu, typeof(ThumbnailMenu), "sortRight", CreateTestButton("Sort right", root.transform));
    SetField(menu, typeof(ThumbnailMenu), "closeButton", CreateTestButton("Close", root.transform));
    SetField(menu, typeof(ThumbnailMenu), "sortText", CreateTestText("Sort label", root.transform));
    SetField(menu, typeof(ThumbnailMenu), "searchField", CreateTestInputField("Search", root.transform));
    SetField(menu, typeof(ThumbnailMenu), "searchClear", CreateTestButton("Clear search", root.transform));
    SetField(menu, typeof(ThumbnailMenu), "thumbnailCategoryPrefab", categoryPrefab);
    SetField(menu, typeof(ThumbnailMenu), "thumbnailCategoryContainer", categoryContainer);
    SetField(menu, typeof(WorkshopMenu), "firstPage", CreateTestButton("First page", root.transform));
    SetField(menu, typeof(WorkshopMenu), "lastPage", CreateTestButton("Last page", root.transform));
    SetField(menu, typeof(WorkshopMenu), "nextPage", CreateTestButton("Next page", root.transform));
    SetField(menu, typeof(WorkshopMenu), "prevPage", CreateTestButton("Previous page", root.transform));
    SetField(menu, typeof(WorkshopMenu), "gameDetailPrefab", gameDetailPrefab);
    PropertyInfo initializedProperty = typeof(SteamRuntime).GetProperty(
      "Initialized", BindingFlags.Static | BindingFlags.Public);
    Assert.IsNotNull(initializedProperty);
    MethodInfo initializedSetter = initializedProperty.GetSetMethod(true);
    bool wasInitialized = SteamRuntime.Initialized;

    try
    {
      Assert.IsNotNull(initializedSetter);
      initializedSetter.Invoke(null, new object[] { false });
      menu.Setup();

      LogAssert.Expect(LogType.Log, "Steam Workshop is not available right now. Did you launch from Steam?");
      menu.Open();
      Assert.IsTrue(menu.IsOpen());
      menu.Close();
      Assert.IsFalse(menu.IsOpen());

      LogAssert.Expect(LogType.Log, "Steam Workshop is not available right now. Did you launch from Steam?");
      menu.Open();
      Assert.IsTrue(menu.IsOpen());
    }
    finally
    {
      initializedSetter.Invoke(null, new object[] { wasInitialized });
      UnityEngine.Object.DestroyImmediate(root);
    }
  }

  [Test]
  public void ActorAndCardWorkshopMenusHandleUnavailableAndEmptyResults()
  {
    PropertyInfo initializedProperty = typeof(SteamRuntime).GetProperty(
      "Initialized", BindingFlags.Static | BindingFlags.Public);
    Assert.IsNotNull(initializedProperty);
    MethodInfo initializedSetter = initializedProperty.GetSetMethod(true);
    Assert.IsNotNull(initializedSetter);
    bool wasInitialized = SteamRuntime.Initialized;

    try
    {
      initializedSetter.Invoke(null, new object[] { false });
      AssertWorkshopSubmenuHandlesFallback(typeof(ActorPrefabWorkshopMenu));
      AssertWorkshopSubmenuHandlesFallback(typeof(CardPackWorkshopMenu));
    }
    finally
    {
      initializedSetter.Invoke(null, new object[] { wasInitialized });
    }
  }

  static void AssertWorkshopSubmenuHandlesFallback(Type menuType)
  {
    GameObject menuObject = new GameObject(menuType.Name + " Offline Test");
    menuObject.SetActive(false);
    Component menu = menuObject.AddComponent(menuType);
    GameObject loadingFeedback = new GameObject("Workshop loading feedback");
    FieldInfo loadingFeedbackField = typeof(ThumbnailMenu).GetField(
      "workingFeedback", BindingFlags.Instance | BindingFlags.NonPublic);
    MethodInfo createThumbnails = menuType.GetMethod(
      "CreateSteamThumbnails", BindingFlags.Instance | BindingFlags.NonPublic);
    MethodInfo callback = menuType.GetMethod(
      "OnWorkshopListLoaded", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo pageCountField = menuType.GetField("pageCount", BindingFlags.Instance | BindingFlags.NonPublic);

    try
    {
      Assert.IsNotNull(loadingFeedbackField);
      Assert.IsNotNull(createThumbnails);
      Assert.IsNotNull(callback);
      Assert.IsNotNull(pageCountField);
      loadingFeedbackField.SetValue(menu, loadingFeedback);

      createThumbnails.Invoke(menu, new object[] { 1u });
      Assert.IsFalse(loadingFeedback.activeSelf, menuType.Name + " kept loading while Steam was unavailable.");

      loadingFeedback.SetActive(true);
      WorkshopItemListEventArgs error = new WorkshopItemListEventArgs(
        new WorkshopItemList(new List<WorkshopItem>(), 0), true, "offline");
      callback.Invoke(menu, new object[] { error });
      Assert.IsFalse(loadingFeedback.activeSelf, menuType.Name + " kept loading after a failed request.");

      loadingFeedbackField.SetValue(menu, null);
      WorkshopItemListEventArgs emptyResult = new WorkshopItemListEventArgs(
        new WorkshopItemList(new List<WorkshopItem>(), 2));
      callback.Invoke(menu, new object[] { emptyResult });
      Assert.AreEqual(2u, pageCountField.GetValue(menu));
    }
    finally
    {
      UnityEngine.Object.DestroyImmediate(menuObject);
      UnityEngine.Object.DestroyImmediate(loadingFeedback);
    }
  }
  static GameObject CreateInactiveObject(string name, Transform parent)
  {
    GameObject created = new GameObject(name, typeof(RectTransform));
    created.SetActive(false);
    created.transform.SetParent(parent, false);
    return created;
  }

  static UnityEngine.UI.Button CreateTestButton(string name, Transform parent)
  {
    GameObject buttonObject = CreateInactiveObject(name, parent);
    buttonObject.AddComponent<CanvasRenderer>();
    buttonObject.AddComponent<UnityEngine.UI.Image>();
    return buttonObject.AddComponent<UnityEngine.UI.Button>();
  }

  static TMPro.TextMeshProUGUI CreateTestText(string name, Transform parent)
  {
    GameObject textObject = CreateInactiveObject(name, parent);
    textObject.AddComponent<CanvasRenderer>();
    return textObject.AddComponent<TMPro.TextMeshProUGUI>();
  }

  static TMPro.TMP_InputField CreateTestInputField(string name, Transform parent)
  {
    GameObject inputObject = CreateInactiveObject(name, parent);
    inputObject.AddComponent<CanvasRenderer>();
    inputObject.AddComponent<UnityEngine.UI.Image>();
    return inputObject.AddComponent<TMPro.TMP_InputField>();
  }

  static void SetField(object target, Type declaringType, string name, object value)
  {
    FieldInfo field = declaringType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    Assert.IsNotNull(field, "Missing field " + declaringType.Name + "." + name);
    field.SetValue(target, value);
  }

}
#endif
#if UNITY_EDITOR && !USE_STEAMWORKS
public class WorkshopMenuNoSteamSortTests
{
  [NUnit.Framework.Test]
  public void NoSteamWorkshopMenusUseNameSortInsteadOfThrowing()
  {
    AssertSortLabel(typeof(WorkshopMenu));
    AssertSortLabel(typeof(ActorPrefabWorkshopMenu));
    AssertSortLabel(typeof(CardPackWorkshopMenu));
  }

  static void AssertSortLabel(System.Type menuType)
  {
    UnityEngine.GameObject menuObject = new UnityEngine.GameObject(menuType.Name + " No Steam Sort Test");
    menuObject.SetActive(false);
    UnityEngine.Component menu = menuObject.AddComponent(menuType);
    UnityEngine.GameObject labelObject = new UnityEngine.GameObject(
      menuType.Name + " Sort Label",
      typeof(UnityEngine.RectTransform),
      typeof(UnityEngine.CanvasRenderer),
      typeof(TMPro.TextMeshProUGUI));
    TMPro.TextMeshProUGUI sortLabel = labelObject.GetComponent<TMPro.TextMeshProUGUI>();
    System.Reflection.FieldInfo sortTextField = typeof(ThumbnailMenu).GetField(
      "sortText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    System.Reflection.MethodInfo changeSort = menuType.GetMethod(
      "ChangeSort", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    try
    {
      NUnit.Framework.Assert.IsNotNull(sortTextField);
      NUnit.Framework.Assert.IsNotNull(changeSort);
      sortTextField.SetValue(menu, sortLabel);
      changeSort.Invoke(menu, new object[] { ThumbnailMenu.Direction.Left });
      NUnit.Framework.Assert.AreEqual("Name (A-Z)", sortLabel.text);
      changeSort.Invoke(menu, new object[] { ThumbnailMenu.Direction.Right });
      NUnit.Framework.Assert.AreEqual("Name (Z-A)", sortLabel.text);
    }
    finally
    {
      UnityEngine.Object.DestroyImmediate(menuObject);
      UnityEngine.Object.DestroyImmediate(labelObject);
    }
  }
}
#endif
