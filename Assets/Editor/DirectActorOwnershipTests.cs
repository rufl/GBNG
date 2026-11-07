#if UNITY_EDITOR && !USE_PUN
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class DirectActorOwnershipTests
{
  static readonly BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
  static readonly MethodInfo ReclaimMethod = typeof(NetworkingController).GetMethod(
    "ReclaimDirectPlayerActors", BindingFlags.Instance | BindingFlags.NonPublic);
  static readonly FieldInfo EngineActorsField = typeof(VoosEngine).GetField("actorsByName", InstanceFields);
  static readonly FieldInfo ActorNetworkingField = typeof(VoosActor).GetField("actorNetworking", InstanceFields);
  static readonly FieldInfo ActorEngineField = typeof(VoosActor).GetField("engine", InstanceFields);
  static readonly FieldInfo ActorViewField = typeof(VoosActor).GetField("reliableView", InstanceFields);

  GameObject engineObject;
  GameObject actorObject;
  GameObject controllerObject;
  PhotonPlayer previousPlayer;
  bool previousOfflineMode;

  [SetUp]
  public void SetUp()
  {
    previousPlayer = PhotonNetwork.player;
    previousOfflineMode = PhotonNetwork.offlineMode;
    PhotonNetwork.player = new PhotonPlayer { ID = 1, NickName = "Host" };
    PhotonNetwork.offlineMode = false;

    engineObject = new GameObject("DirectActorOwnershipTests Engine");
    engineObject.SetActive(false);
    VoosEngine engine = engineObject.AddComponent<VoosEngine>();

    actorObject = new GameObject("DirectActorOwnershipTests Actor");
    actorObject.SetActive(false);
    Rigidbody body = actorObject.AddComponent<Rigidbody>();
    PhotonView view = actorObject.AddComponent<PhotonView>();
    view.ownerId = 2;
    VoosActor actor = actorObject.AddComponent<VoosActor>();
    ActorViewField.SetValue(actor, view);
    ActorEngineField.SetValue(actor, engine);
    ActorNetworkingField.SetValue(actor, actorObject.AddComponent<ActorNetworking>());
    typeof(VoosActor).GetField("enablePhysics", InstanceFields).SetValue(actor, true);
    actor.SetReplicantCatchUpMode(true);
    Assert.IsTrue(body.isKinematic, "Remote-owned catch-up replicants should be kinematic before reclaim.");

    IDictionary actors = (IDictionary)EngineActorsField.GetValue(engine);
    actors.Add("ownership-test", actor);

    controllerObject = new GameObject("DirectActorOwnershipTests Controller");
    controllerObject.SetActive(false);
    NetworkingController controller = controllerObject.AddComponent<NetworkingController>();
    controller.voosEngine = engine;
  }

  [TearDown]
  public void TearDown()
  {
    PhotonNetwork.player = previousPlayer;
    PhotonNetwork.offlineMode = previousOfflineMode;
    if (controllerObject != null) Object.DestroyImmediate(controllerObject);
    if (actorObject != null) Object.DestroyImmediate(actorObject);
    if (engineObject != null) Object.DestroyImmediate(engineObject);
  }

  [Test]
  public void DisconnectReclaimsActorAndReactivatesLocalPhysics()
  {
    Assert.IsNotNull(ReclaimMethod);
    ReclaimMethod.Invoke(controllerObject.GetComponent<NetworkingController>(), new object[] { 2 });
    PhotonView view = actorObject.GetComponent<PhotonView>();
    Rigidbody body = actorObject.GetComponent<Rigidbody>();
    Assert.AreEqual(1, view.ownerId);
    Assert.IsFalse(body.isKinematic, "Reclaimed locally-owned actors must leave remote catch-up physics mode.");
  }
}
#endif
