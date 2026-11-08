using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class GamepadUINavigation : MonoBehaviour
{
  const float DpadDeadzone = 0.5f;
  const float InitialRepeatDelay = 0.5f;
  const float RepeatInterval = 0.1f;

  static GamepadUINavigation activeNavigation;

  EventSystem eventSystem;
  AxisEventData moveEventData;
  BaseEventData buttonEventData;
  Vector2 lastMoveDirection;
  float nextMoveTime;

  public static bool IsConsumingFaceButtons
  {
    get
    {
      GamepadUINavigation active = activeNavigation;
      EventSystem currentEventSystem = active == null ? null : active.eventSystem;
      GameObject selected = currentEventSystem == null ? null : currentEventSystem.currentSelectedGameObject;
      return selected != null && selected.activeInHierarchy;
    }
  }

  void Awake()
  {
    activeNavigation = this;
    Initialize();
  }

  void OnEnable()
  {
    activeNavigation = this;
    Initialize();
  }

  void OnDisable()
  {
    if (activeNavigation == this) activeNavigation = null;
  }

  void Initialize()
  {
    if (eventSystem == null) eventSystem = GetComponent<EventSystem>();
    if (eventSystem == null) eventSystem = EventSystem.current;
    if (eventSystem == null) return;

    if (moveEventData == null) moveEventData = new AxisEventData(eventSystem);
    if (buttonEventData == null) buttonEventData = new BaseEventData(eventSystem);
  }

  void Update()
  {
    Gamepad gamepad = Gamepad.current;
    if (eventSystem == null || !eventSystem.isActiveAndEnabled || gamepad == null)
    {
      lastMoveDirection = Vector2.zero;
      return;
    }

    if (HasGameplayAxisInput(gamepad))
    {
      if (eventSystem.currentSelectedGameObject != null)
      {
        eventSystem.SetSelectedGameObject(null);
      }
      lastMoveDirection = Vector2.zero;
      return;
    }
    MoveSelection(gamepad);

    GameObject selected = eventSystem.currentSelectedGameObject;
    if (selected == null || !selected.activeInHierarchy) return;

    if (gamepad.buttonSouth.wasPressedThisFrame)
    {
      buttonEventData.Reset();
      ExecuteEvents.ExecuteHierarchy(selected, buttonEventData, ExecuteEvents.submitHandler);
    }

    if (gamepad.buttonEast.wasPressedThisFrame)
    {
      buttonEventData.Reset();
      ExecuteEvents.ExecuteHierarchy(selected, buttonEventData, ExecuteEvents.cancelHandler);
    }
  }

  static bool HasGameplayAxisInput(Gamepad gamepad)
  {
    return gamepad.leftStick.ReadValue().sqrMagnitude > 0.01f ||
      gamepad.rightStick.ReadValue().sqrMagnitude > 0.01f ||
      gamepad.leftTrigger.ReadValue() > 0.1f ||
      gamepad.rightTrigger.ReadValue() > 0.1f;
  }

  void MoveSelection(Gamepad gamepad)
  {
    Vector2 direction = ReadDpadDirection(gamepad.dpad.ReadValue());
    if (direction == Vector2.zero)
    {
      lastMoveDirection = Vector2.zero;
      return;
    }

    float now = Time.unscaledTime;
    bool directionChanged = direction != lastMoveDirection;
    if (!directionChanged && now < nextMoveTime) return;

    lastMoveDirection = direction;
    nextMoveTime = now + (directionChanged ? InitialRepeatDelay : RepeatInterval);

    GameObject selected = eventSystem.currentSelectedGameObject;
    if (selected == null || !selected.activeInHierarchy)
    {
      eventSystem.SetSelectedGameObject(FindFirstNavigableSelectable());
      return;
    }

    Selectable currentSelectable = selected.GetComponent<Selectable>();
    if (currentSelectable != null)
    {
      Selectable nextSelectable = direction.x < 0f
        ? currentSelectable.FindSelectableOnLeft()
        : direction.x > 0f
          ? currentSelectable.FindSelectableOnRight()
          : direction.y < 0f
            ? currentSelectable.FindSelectableOnDown()
            : currentSelectable.FindSelectableOnUp();
      if (nextSelectable != null && nextSelectable.isActiveAndEnabled && nextSelectable.IsInteractable())
      {
        eventSystem.SetSelectedGameObject(nextSelectable.gameObject);
        return;
      }
    }

    moveEventData.Reset();
    moveEventData.moveVector = direction;
    moveEventData.moveDir = DirectionFor(direction);
    if (ExecuteEvents.Execute(selected, moveEventData, ExecuteEvents.moveHandler))
    {
      GameObject nextSelected = moveEventData.selectedObject;
      if (nextSelected != null && nextSelected.activeInHierarchy)
      {
        eventSystem.SetSelectedGameObject(nextSelected);
      }
    }

  }

  static Vector2 ReadDpadDirection(Vector2 value)
  {
    if (Mathf.Abs(value.x) > Mathf.Abs(value.y))
    {
      if (Mathf.Abs(value.x) < DpadDeadzone) return Vector2.zero;
      return value.x < 0f ? Vector2.left : Vector2.right;
    }

    if (Mathf.Abs(value.y) < DpadDeadzone) return Vector2.zero;
    return value.y < 0f ? Vector2.down : Vector2.up;
  }

  static MoveDirection DirectionFor(Vector2 direction)
  {
    if (direction.x < 0f) return MoveDirection.Left;
    if (direction.x > 0f) return MoveDirection.Right;
    return direction.y < 0f ? MoveDirection.Down : MoveDirection.Up;
  }

  static GameObject FindFirstNavigableSelectable()
  {
    Selectable[] selectables = Selectable.allSelectablesArray;
    for (int i = 0; i < selectables.Length; i++)
    {
      Selectable selectable = selectables[i];
      if (selectable != null && selectable.isActiveAndEnabled && selectable.interactable &&
        selectable.navigation.mode != Navigation.Mode.None)
      {
        return selectable.gameObject;
      }
    }

    return null;
  }
}
