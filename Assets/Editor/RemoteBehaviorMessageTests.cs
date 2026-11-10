#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class RemoteBehaviorMessageTests
{
  static readonly BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
  static readonly FieldInfo StateField = typeof(VoosEngine).GetField("state", InstancePrivate);
  static readonly FieldInfo MessagesField = typeof(VoosEngine).GetField("messagesFromUnity", InstancePrivate);
  static readonly MethodInfo ReceiveMessagesMethod = typeof(VoosEngine).GetMethod(
    "HandleMessagesToMyActorsRPC", InstancePrivate);

  [Test]
  public void RemoteBehaviorBroadcastPreservesSender()
  {
    Assert.IsNotNull(StateField);
    Assert.IsNotNull(MessagesField);
    Assert.IsNotNull(ReceiveMessagesMethod);

    GameObject owner = new GameObject("RemoteBehaviorMessageTests");
    owner.SetActive(false);
    try
    {
      VoosEngine engine = owner.AddComponent<VoosEngine>();
      StateField.SetValue(engine, Enum.Parse(StateField.FieldType, "Init"));

      ReceiveMessagesMethod.Invoke(engine, new object[] {
        "{\"messages\":[{\"viewId\":-1,\"messageName\":\"Example\",\"argsJson\":\"{}\",\"senderActor\":\"sender-actor\"}]}"
      });

      List<VoosEngine.ActorMessage> queued = (List<VoosEngine.ActorMessage>)MessagesField.GetValue(engine);
      Assert.That(queued, Has.Count.EqualTo(1));
      Assert.AreEqual("Example", queued[0].name);
      Assert.AreEqual("sender-actor", queued[0].senderActor,
        "A remote behavior must receive messageSender from the originating actor.");
      Assert.IsTrue(queued[0].fromRemote, "Incoming broadcasts must not be sent back to remote peers.");
    }
    finally
    {
      UnityEngine.Object.DestroyImmediate(owner);
    }
  }
}
#endif
