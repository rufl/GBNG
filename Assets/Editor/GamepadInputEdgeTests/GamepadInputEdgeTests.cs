using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

public class GamepadInputEdgeTests : InputTestFixture
{
  static readonly System.Type GameBuilderInputControlType =
    System.Type.GetType("InputControl, Assembly-CSharp", true);
  static readonly System.Type UserMainType = System.Type.GetType("UserMain, Assembly-CSharp", true);
  static readonly System.Type EditMainType = System.Type.GetType("EditMain, Assembly-CSharp", true);
  static readonly System.Type GamepadUINavigationType =
    System.Type.GetType("GamepadUINavigation, Assembly-CSharp", true);
  static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
  static readonly MethodInfo GetButtonDownMethod = GameBuilderInputControlType.GetMethod("GetButtonDown");
  static readonly MethodInfo GetButtonMethod = GameBuilderInputControlType.GetMethod("GetButton");
  static readonly MethodInfo GetButtonUpMethod = GameBuilderInputControlType.GetMethod("GetButtonUp");
  static readonly MethodInfo UpdateGamepadUINavigationMethod =
    GamepadUINavigationType.GetMethod("Update", PrivateInstance);
  static readonly MethodInfo AwakeGamepadUINavigationMethod =
    GamepadUINavigationType.GetMethod("Awake", PrivateInstance);
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

  [Test]
  public void DpadNavigatesAndSelectedUiButtonsDoNotTriggerGameplayActions()
  {
    Gamepad gamepad = InputSystem.AddDevice<Gamepad>();
    GameObject eventSystemObject = new GameObject("GamepadInputEdgeTests EventSystem");
    EventSystem eventSystem = eventSystemObject.AddComponent<EventSystem>();
    Component navigation = eventSystemObject.AddComponent(GamepadUINavigationType);
    AwakeGamepadUINavigationMethod.Invoke(navigation, null);
    GameObject targetObject = new GameObject("GamepadInputEdgeTests Selected UI");
    Button targetButton = targetObject.AddComponent<Button>();
    GameObject rightTargetObject = new GameObject("GamepadInputEdgeTests Right UI");
    rightTargetObject.SetActive(false);
    Button rightTarget = rightTargetObject.AddComponent<Button>();
    Navigation targetNavigation = targetButton.navigation;
    targetNavigation.mode = Navigation.Mode.Explicit;
    targetNavigation.selectOnRight = rightTarget;
    targetButton.navigation = targetNavigation;
    int submitCount = 0;
    targetButton.onClick.AddListener(() => submitCount++);
    rightTarget.onClick.AddListener(() => submitCount++);
    GamepadUiEventRecorder recorder = rightTargetObject.AddComponent<GamepadUiEventRecorder>();

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
      Press(gamepad.dpad.right);
      UpdateGamepadUINavigationMethod.Invoke(navigation, null);
      Assert.AreSame(targetObject, eventSystem.currentSelectedGameObject);
      Release(gamepad.dpad.right);
      UpdateGamepadUINavigationMethod.Invoke(navigation, null);
      rightTargetObject.SetActive(true);
      Press(gamepad.dpad.right);
      UpdateGamepadUINavigationMethod.Invoke(navigation, null);
      Assert.AreSame(rightTargetObject, eventSystem.currentSelectedGameObject);
      Release(gamepad.dpad.right);
      UpdateGamepadUINavigationMethod.Invoke(navigation, null);

      Press(gamepad.buttonSouth);
      UpdateGamepadUINavigationMethod.Invoke(navigation, null);
      Assert.AreEqual(1, submitCount);
      Assert.IsFalse(ReadAction(GetButtonDownMethod, input, "Jump"));

      Release(gamepad.buttonSouth);
      UpdateGamepadUINavigationMethod.Invoke(navigation, null);

      Press(gamepad.buttonEast);
      UpdateGamepadUINavigationMethod.Invoke(navigation, null);
      Assert.AreEqual(1, recorder.CancelCount);
      Assert.IsFalse(ReadAction(GetButtonDownMethod, input, "Action2"));

      Release(gamepad.buttonEast);
      UpdateGamepadUINavigationMethod.Invoke(navigation, null);

      Press(gamepad.buttonWest);
      UpdateGamepadUINavigationMethod.Invoke(navigation, null);
      Assert.IsFalse(ReadAction(GetButtonDownMethod, input, "Action1"));
      Release(gamepad.buttonWest);
      UpdateGamepadUINavigationMethod.Invoke(navigation, null);

      Press(gamepad.buttonNorth);
      UpdateGamepadUINavigationMethod.Invoke(navigation, null);
      Assert.IsFalse(ReadAction(GetButtonDownMethod, input, "View"));
    }
    finally
    {
      Object.DestroyImmediate(inputObject);
      Object.DestroyImmediate(userMainObject);
      Object.DestroyImmediate(editMainObject);
      Object.DestroyImmediate(targetObject);
      Object.DestroyImmediate(rightTargetObject);
      Object.DestroyImmediate(eventSystemObject);
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

  class GamepadUiEventRecorder : MonoBehaviour, ICancelHandler
  {
    public int CancelCount;

    public void OnCancel(BaseEventData eventData)
    {
      CancelCount++;
    }
  }
}
