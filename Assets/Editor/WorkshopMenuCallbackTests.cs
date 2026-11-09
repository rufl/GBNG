#if UNITY_EDITOR && USE_STEAMWORKS
using System;
using System.Collections.Generic;
using System.Reflection;
using GameBuilder.Workshop;
using NUnit.Framework;
using UnityEngine;

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

    callback.Invoke(menu, new[] { args, itemListData });
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
      callback.Invoke(menu, new[] { args, Activator.CreateInstance(itemListDataType) });
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
      callback.Invoke(menu, new[] { args, Activator.CreateInstance(itemListDataType) });
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
}
#endif
