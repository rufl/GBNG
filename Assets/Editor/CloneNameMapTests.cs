#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class CloneNameMapTests
{
  static readonly BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
  static readonly MethodInfo BuildMapMethod = typeof(VoosEngine).GetMethod(
    "BuildClonedActorNameDict", BindingFlags.Instance | BindingFlags.NonPublic);
  static readonly FieldInfo ActorNameField = typeof(VoosActor).GetField("voosName", InstanceFields);

  GameObject engineObject;
  GameObject rootObject;
  GameObject childObject;

  [TearDown]
  public void TearDown()
  {
    if (childObject != null) Object.DestroyImmediate(childObject);
    if (rootObject != null) Object.DestroyImmediate(rootObject);
    if (engineObject != null) Object.DestroyImmediate(engineObject);
  }

  [Test]
  public void CloneNameMapIncludesEveryDescendantOnce()
  {
    Assert.IsNotNull(BuildMapMethod);
    Assert.IsNotNull(ActorNameField);

    engineObject = new GameObject("CloneNameMapTests Engine");
    engineObject.SetActive(false);
    VoosEngine engine = engineObject.AddComponent<VoosEngine>();
    rootObject = new GameObject("CloneNameMapTests Root");
    rootObject.SetActive(false);
    VoosActor root = rootObject.AddComponent<VoosActor>();
    ActorNameField.SetValue(root, "root");
    childObject = new GameObject("CloneNameMapTests Child");
    childObject.SetActive(false);
    childObject.transform.SetParent(rootObject.transform);
    VoosActor child = childObject.AddComponent<VoosActor>();
    ActorNameField.SetValue(child, "child");

    var map = (Dictionary<string, string>)BuildMapMethod.Invoke(engine, new object[] { new[] { root } });

    Assert.IsTrue(map.ContainsKey("root"));
    Assert.IsTrue(map.ContainsKey("child"));
    Assert.AreNotEqual(map["root"], map["child"]);
  }
}
#endif
