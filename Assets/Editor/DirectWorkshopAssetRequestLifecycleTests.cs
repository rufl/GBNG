#if UNITY_EDITOR && !USE_STEAMWORKS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class DirectWorkshopAssetRequestLifecycleTests
{
  static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
  static readonly FieldInfo CallbacksField = typeof(NetworkingController).GetField(
    "directWorkshopAssetCallbacks", PrivateInstance);
  static readonly FieldInfo DeadlinesField = typeof(NetworkingController).GetField(
    "directWorkshopAssetDeadlines", PrivateInstance);
  static readonly MethodInfo ExpireMethod = typeof(NetworkingController).GetMethod(
    "ExpireDirectWorkshopAssetRequests", PrivateInstance);
  static readonly MethodInfo FailMethod = typeof(NetworkingController).GetMethod(
    "FailOutstandingDirectWorkshopAssetRequests", PrivateInstance);

  GameObject controllerObject;
  NetworkingController controller;

  [SetUp]
  public void SetUp()
  {
    controllerObject = new GameObject("DirectWorkshopAssetRequestLifecycleTests");
    controllerObject.SetActive(false);
    controller = controllerObject.AddComponent<NetworkingController>();
    Assert.IsNotNull(CallbacksField);
    Assert.IsNotNull(DeadlinesField);
    Assert.IsNotNull(ExpireMethod);
    Assert.IsNotNull(FailMethod);
  }

  [TearDown]
  public void TearDown()
  {
    if (controllerObject != null) UnityEngine.Object.DestroyImmediate(controllerObject);
  }

  [Test]
  public void ExpiredRequestCompletesEveryCoalescedCallback()
  {
    var first = new List<Util.Maybe<string>>();
    var second = new List<Util.Maybe<string>>();
    AddPendingRequest(17, 4f, first, second);

    ExpireMethod.Invoke(controller, new object[] { 4f });

    Assert.AreEqual(1, first.Count);
    Assert.AreEqual(1, second.Count);
    Assert.IsTrue(first[0].IsEmpty());
    Assert.IsTrue(second[0].IsEmpty());
    Assert.AreEqual("The direct host did not respond before the asset request timed out.", first[0].GetErrorMessage());
    Assert.AreEqual(0, GetCallbacks().Count);
    Assert.AreEqual(0, GetDeadlines().Count);
  }

  [Test]
  public void DisconnectCompletesEveryOutstandingAssetRequest()
  {
    var firstAsset = new List<Util.Maybe<string>>();
    var secondAssetFirstCallback = new List<Util.Maybe<string>>();
    var secondAssetSecondCallback = new List<Util.Maybe<string>>();
    AddPendingRequest(4, 10f, firstAsset);
    AddPendingRequest(9, 10f, secondAssetFirstCallback, secondAssetSecondCallback);

    FailMethod.Invoke(controller, new object[] { "The connection closed." });

    Assert.AreEqual(1, firstAsset.Count);
    Assert.AreEqual(1, secondAssetFirstCallback.Count);
    Assert.AreEqual(1, secondAssetSecondCallback.Count);
    Assert.IsTrue(firstAsset[0].IsEmpty());
    Assert.IsTrue(secondAssetFirstCallback[0].IsEmpty());
    Assert.IsTrue(secondAssetSecondCallback[0].IsEmpty());
    Assert.AreEqual("The connection closed.", firstAsset[0].GetErrorMessage());
    Assert.AreEqual("The connection closed.", secondAssetFirstCallback[0].GetErrorMessage());
    Assert.AreEqual("The connection closed.", secondAssetSecondCallback[0].GetErrorMessage());
    Assert.AreEqual(0, GetCallbacks().Count);
    Assert.AreEqual(0, GetDeadlines().Count);
  }

  void AddPendingRequest(ulong assetId, float deadline, params List<Util.Maybe<string>>[] results)
  {
    var callbacks = new List<Action<Util.Maybe<string>>>();
    foreach (List<Util.Maybe<string>> resultsForCallback in results)
      callbacks.Add(result => resultsForCallback.Add(result));
    GetCallbacks().Add(assetId, callbacks);
    GetDeadlines().Add(assetId, deadline);
  }

  IDictionary GetCallbacks()
  {
    return (IDictionary)CallbacksField.GetValue(controller);
  }

  IDictionary GetDeadlines()
  {
    return (IDictionary)DeadlinesField.GetValue(controller);
  }
}
#endif
