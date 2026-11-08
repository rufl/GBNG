using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

public class GamepadInputEdgeTests : InputTestFixture
{
  static readonly System.Type GameBuilderInputControlType =
    System.Type.GetType("InputControl, Assembly-CSharp", true);
  static readonly System.Type UserMainType = System.Type.GetType("UserMain, Assembly-CSharp", true);
  static readonly System.Type EditMainType = System.Type.GetType("EditMain, Assembly-CSharp", true);
  static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
  static readonly MethodInfo GetButtonDownMethod = GameBuilderInputControlType.GetMethod("GetButtonDown");
  static readonly MethodInfo GetButtonMethod = GameBuilderInputControlType.GetMethod("GetButton");
  static readonly MethodInfo GetButtonUpMethod = GameBuilderInputControlType.GetMethod("GetButtonUp");
  static readonly FieldInfo InputControlUserMainField =
    GameBuilderInputControlType.GetField("userMain", PrivateInstance);
  static readonly FieldInfo UserMainEditMainField = UserMainType.GetField("editMain", PrivateInstance);

  [Test]
  public void GameplayActionsTrackVirtualGamepadPressAndReleaseEdges()
  {
    Gamepad gamepad = InputSystem.AddDevice<Gamepad>();
    GameObject inputObject = new GameObject("GamepadInputEdgeTests Input");
    Component input = inputObject.AddComponent(GameBuilderInputControlType);

    GameObject userMainObject = new GameObject("GamepadInputEdgeTests UserMain");
    userMainObject.SetActive(false);
    Component userMain = userMainObject.AddComponent(UserMainType);
    GameObject editMainObject = new GameObject("GamepadInputEdgeTests EditMain");
    editMainObject.SetActive(false);
    Component editMain = editMainObject.AddComponent(EditMainType);
    UserMainEditMainField.SetValue(userMain, editMain);
    InputControlUserMainField.SetValue(input, userMain);

    try
    {
      AssertActionEdges(gamepad.buttonSouth, input, "Jump");
      AssertActionEdges(gamepad.buttonNorth, input, "View");
      AssertActionEdges(gamepad.buttonWest, input, "Action1");
      AssertActionEdges(gamepad.buttonEast, input, "Action2");
      AssertActionEdges(gamepad.leftStickButton, input, "Sprint");
      AssertActionEdges(gamepad.startButton, input, "Pause");
    }
    finally
    {
      Object.DestroyImmediate(inputObject);
      Object.DestroyImmediate(userMainObject);
      Object.DestroyImmediate(editMainObject);
    }
  }

  void AssertActionEdges(
    UnityEngine.InputSystem.Controls.ButtonControl button,
    Component input,
    string action)
  {
    Assert.IsFalse(ReadAction(GetButtonDownMethod, input, action), action + " must start released");

    Press(button);
    Assert.IsTrue(ReadAction(GetButtonDownMethod, input, action), action + " must report press edge");
    Assert.IsTrue(ReadAction(GetButtonMethod, input, action), action + " must be held");

    Release(button);
    Assert.IsTrue(ReadAction(GetButtonUpMethod, input, action), action + " must report release edge");
    Assert.IsFalse(ReadAction(GetButtonMethod, input, action), action + " must finish released");
  }

  static bool ReadAction(MethodInfo method, Component input, string action)
  {
    return (bool)method.Invoke(input, new object[] { action });
  }
}
