#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class MultiplayerProjectLibraryInitTests
{
  const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

  GameObject controllerObject;
  GameObject behaviorObject;
  GameObject actorLibraryObject;
  NetworkingController controller;
  BehaviorSystem behaviorSystem;
  SceneActorLibrary actorLibrary;

  [SetUp]
  public void SetUp()
  {
    controllerObject = new GameObject("Multiplayer library init controller");
    behaviorObject = new GameObject("Multiplayer library init behaviors");
    actorLibraryObject = new GameObject("Multiplayer library init actors");
    controllerObject.SetActive(false);
    behaviorObject.SetActive(false);
    actorLibraryObject.SetActive(false);
    controller = controllerObject.AddComponent<NetworkingController>();
    behaviorSystem = behaviorObject.AddComponent<BehaviorSystem>();
    actorLibrary = actorLibraryObject.AddComponent<SceneActorLibrary>();
    controller.behaviorSystem = behaviorSystem;
    GetField(typeof(NetworkingController), "sceneActorLibrary").SetValue(controller, actorLibrary);
  }

  [TearDown]
  public void TearDown()
  {
    UnityEngine.Object.DestroyImmediate(controllerObject);
    UnityEngine.Object.DestroyImmediate(behaviorObject);
    UnityEngine.Object.DestroyImmediate(actorLibraryObject);
  }

  [Test]
  public void JoiningPlayerReceivesProjectCardAndActorPacks()
  {
    Type payloadType = typeof(NetworkingController).GetNestedType("NewPlayerInitPayload", BindingFlags.NonPublic);
    Assert.IsNotNull(payloadType);
    object payload = Activator.CreateInstance(payloadType, true);
    const ulong cardPackId = 71;
    const ulong actorPackId = 72;
    var cardPacks = new BehaviorSystem.SavedCardPacks();
    cardPacks.cardPacks.Add(new BehaviorSystem.SavedCardPack(cardPackId, "Shared cards", "", new List<string> { "builtin:Default Behavior" }));
    var actorPacks = new SceneActorLibrary.SavedActorPacks();
    actorPacks.actorPacks.Add(new SceneActorLibrary.SavedActorPack(actorPackId, "Shared actors", "", new List<string> { "shared-actor" }));
    GetField(payloadType, "cardPacks").SetValue(payload, cardPacks);
    GetField(payloadType, "actorPacks").SetValue(payload, actorPacks);

    MethodInfo loadLibraries = typeof(NetworkingController).GetMethod("LoadNewPlayerProjectLibraries", PrivateInstance);
    Assert.IsNotNull(loadLibraries);
    loadLibraries.Invoke(controller, new[] { payload });

    Assert.AreEqual("Shared cards", behaviorSystem.GetCardPack(cardPackId).workshopName);
    Assert.AreEqual("Shared actors", actorLibrary.GetActorPack(actorPackId).workshopName);
  }

  static FieldInfo GetField(Type type, string name)
  {
    FieldInfo field = type.GetField(name, PrivateInstance | BindingFlags.Public);
    Assert.IsNotNull(field, "Missing field: " + name);
    return field;
  }
}
#endif
