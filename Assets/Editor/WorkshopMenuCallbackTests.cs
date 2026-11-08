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
    UnityEngine.Object.DestroyImmediate(menuObject);

    MethodInfo callback = typeof(WorkshopMenu).GetMethod(
      "OnWorkshopListLoaded", BindingFlags.Instance | BindingFlags.NonPublic);
    Type itemListDataType = typeof(WorkshopMenu).GetNestedType("ItemListData", BindingFlags.NonPublic);
    Assert.IsNotNull(callback);
    Assert.IsNotNull(itemListDataType);
    object itemListData = Activator.CreateInstance(itemListDataType);
    WorkshopItemListEventArgs args = new WorkshopItemListEventArgs(
      new WorkshopItemList(new List<WorkshopItem>(), 1));

    Assert.DoesNotThrow(() => callback.Invoke(menu, new[] { args, itemListData }));
  }
}
#endif
