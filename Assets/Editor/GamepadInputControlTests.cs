#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem;

public class GamepadInputControlTests
{
  static readonly BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

  Gamepad gamepad;
  InputControl inputControl;
  GameObject inputObject;
  GameObject userMainObject;
  GameObject editMainObject;
  UserMain userMain;

  [SetUp]
  public void SetUp()
  {
    gamepad = InputSystem.AddDevice<Gamepad>();
    inputObject = new GameObject("GamepadInputControlTests Input");
    inputControl = inputObject.AddComponent<InputControl>();

    userMainObject = new GameObject("GamepadInputControlTests UserMain");
    userMainObject.SetActive(false);
    userMain = userMainObject.AddComponent<UserMain>();
    userMain.playerOptions = new PlayerOptions { mouseLookSensitivity = 0.5f };

    editMainObject = new GameObject("GamepadInputControlTests EditMain");
    editMainObject.SetActive(false);
    EditMain editMain = editMainObject.AddComponent<EditMain>();
    typeof(UserMain).GetField("editMain", InstanceFields).SetValue(userMain, editMain);
    typeof(InputControl).GetField("userMain", InstanceFields).SetValue(inputControl, userMain);
  }

  [TearDown]
  public void TearDown()
  {
    if (inputObject != null) Object.DestroyImmediate(inputObject);
    if (userMainObject != null) Object.DestroyImmediate(userMainObject);
    if (editMainObject != null) Object.DestroyImmediate(editMainObject);
    if (gamepad != null)
    {
      InputSystem.RemoveDevice(gamepad);
      InputSystem.Update();
    }
  }

  void QueueState(GamepadState state)
  {
    InputSystem.QueueStateEvent(gamepad, state);
    InputSystem.Update();
  }


  [Test]
  public void FaceButtonsDriveGameplayActionsWhileHeldAndReleased()
  {
    QueueState(new GamepadState()
      .WithButton(GamepadButton.South)
      .WithButton(GamepadButton.West)
      .WithButton(GamepadButton.East)
      .WithButton(GamepadButton.LeftStick));

    Assert.IsTrue(inputControl.GetButton("Jump"));
    Assert.IsTrue(inputControl.GetButton("Action1"));
    Assert.IsTrue(inputControl.GetButton("Action2"));
    Assert.IsTrue(inputControl.GetButton("Sprint"));

    QueueState(new GamepadState());
    Assert.IsFalse(inputControl.GetButton("Jump"));
    Assert.IsFalse(inputControl.GetButton("Action1"));
    Assert.IsFalse(inputControl.GetButton("Action2"));
    Assert.IsFalse(inputControl.GetButton("Sprint"));
  }

  [Test]
  public void NorthAndStartButtonsDriveViewAndPauseWhileHeldAndReleased()
  {
    QueueState(new GamepadState()
      .WithButton(GamepadButton.North)
      .WithButton(GamepadButton.Start));

    Assert.IsTrue(inputControl.GetButton("View"));
    Assert.IsTrue(inputControl.GetButton("Pause"));

    QueueState(new GamepadState());
    Assert.IsFalse(inputControl.GetButton("View"));
    Assert.IsFalse(inputControl.GetButton("Pause"));
  }


  [Test]
  public void GameplayFaceButtonsDoNotTriggerBuildModeActions()
  {
    editMainObject.SetActive(true);
    QueueState(new GamepadState()
      .WithButton(GamepadButton.South)
      .WithButton(GamepadButton.West)
      .WithButton(GamepadButton.East));

    Assert.IsFalse(inputControl.GetButtonDown("Jump"));
    Assert.IsFalse(inputControl.GetButtonDown("Action1"));
    Assert.IsFalse(inputControl.GetButtonDown("Action2"));
  }

  [Test]
  public void LeftStickAndTriggersMapToMovementAxes()
  {
    QueueState(new GamepadState
    {
      leftStick = new Vector2(0.35f, 0.75f),
      rightTrigger = 0.8f
    });

    Vector2 stick = gamepad.leftStick.ReadValue();
    Vector3 move = inputControl.GetMoveAxes();
    Assert.AreEqual(stick.x, move.x, 0.001f);
    Assert.AreEqual(stick.y, move.z, 0.001f);
    Assert.That(move.y, Is.GreaterThan(0.7f));

    QueueState(new GamepadState { leftTrigger = 0.8f });
    Assert.That(inputControl.GetMoveAxes().y, Is.LessThan(-0.7f));
  }

  [Test]
  public void RightStickMapsToLookWithSensitivityAndInvertSettings()
  {
    userMain.playerOptions = new PlayerOptions
    {
      mouseLookSensitivity = 0.5f,
      invertMouselook = true
    };
    QueueState(new GamepadState { rightStick = new Vector2(0.4f, 0.6f) });

    Vector2 mouseAxes = inputControl.GetMouseAxes();
    Vector2 stick = gamepad.rightStick.ReadValue();
    Vector2 look = inputControl.GetLookAxes();
    Assert.AreEqual((mouseAxes.x + stick.x) * 0.5f, look.x, 0.001f);
    Assert.AreEqual(-(mouseAxes.y + stick.y) * 0.5f, look.y, 0.001f);
    Assert.IsTrue(inputControl.IsGamepadLookActive());

    QueueState(new GamepadState());
    Assert.IsFalse(inputControl.IsGamepadLookActive());
  }
}

#endif
