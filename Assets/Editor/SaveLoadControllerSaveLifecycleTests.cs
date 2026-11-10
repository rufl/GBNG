#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Jobs;
using UnityEngine;

public class SaveLoadControllerSaveLifecycleTests
{
  const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

  [Test]
  public void SaveRequestWaitsForScheduledWrite()
  {
    GameObject owner = new GameObject("Save queue lifecycle test");
    owner.SetActive(false);
    SaveLoadController controller = owner.AddComponent<SaveLoadController>();
    Type requestType = typeof(SaveLoadController).GetNestedType("SaveRequest", BindingFlags.NonPublic);
    Assert.IsNotNull(requestType);
    object request = Activator.CreateInstance(requestType, true);
    int callbackCount = 0;

    try
    {
      GetField(requestType, "onSaveComplete").SetValue(request, (Action)(() => callbackCount++));
      GetField(typeof(SaveLoadController), "activeSaveRequest").SetValue(controller, request);

      InvokeUpdate(controller);

      Assert.AreSame(request, GetField(typeof(SaveLoadController), "activeSaveRequest").GetValue(controller));
      Assert.AreEqual(0, callbackCount, "A save must not complete before its file-write job is scheduled.");

      GetField(typeof(SaveLoadController), "activeWriteStarted").SetValue(controller, true);
      GetField(typeof(SaveLoadController), "activeWriteJobHandle").SetValue(controller, new JobHandle());
      InvokeUpdate(controller);

      Assert.IsNull(GetField(typeof(SaveLoadController), "activeSaveRequest").GetValue(controller));
      Assert.AreEqual(1, callbackCount, "A scheduled, completed write must release its queued save request.");
    }
    finally
    {
      UnityEngine.Object.DestroyImmediate(owner);
    }
  }

  static FieldInfo GetField(Type type, string name)
  {
    FieldInfo field = type.GetField(name, PrivateInstance | BindingFlags.Public);
    Assert.IsNotNull(field, "Missing field: " + name);
    return field;
  }

  static void InvokeUpdate(SaveLoadController controller)
  {
    MethodInfo update = typeof(SaveLoadController).GetMethod("Update", PrivateInstance);
    Assert.IsNotNull(update);
    update.Invoke(controller, null);
  }
}
#endif
