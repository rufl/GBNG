#if UNITY_EDITOR
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

public class CardDetailAnimationTests
{
  const string PrefabPath = "Assets/Prefabs/UI/Inspector/CardTabPrefabs/CardDetailCanvas.prefab";

  GameObject instance;
  CardDetail detail;
  RectTransform panel;
  CanvasGroup canvasGroup;
  MethodInfo closeAnimated;

  void CreateDetail()
  {
    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
    Assert.IsNotNull(prefab, "Card detail prefab must be available to the editor tests.");

    instance = Object.Instantiate(prefab);
    detail = instance.GetComponent<CardDetail>();
    Assert.IsNotNull(detail);
    detail.Setup(instance.GetComponent<RectTransform>());
    panel = GetField<RectTransform>(detail, "rectTransform");
    canvasGroup = GetField<CanvasGroup>(detail, "canvasGroup");
    closeAnimated = typeof(CardDetail).GetMethod("CloseAnimated", BindingFlags.Instance | BindingFlags.NonPublic);
    Assert.IsNotNull(closeAnimated);
  }

  static T GetField<T>(CardDetail target, string name) where T : class
  {
    FieldInfo field = typeof(CardDetail).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    Assert.IsNotNull(field, "Expected CardDetail field " + name);
    T value = field.GetValue(target) as T;
    Assert.IsNotNull(value, "CardDetail field " + name + " must be assigned in its prefab.");
    return value;
  }

  [TearDown]
  public void TearDown()
  {
    if (instance != null) Object.DestroyImmediate(instance);
  }

  [UnityTest]
  public IEnumerator CloseAnimationFadesOutAndThenDeactivatesPanel()
  {
    CreateDetail();
    detail.Open(0);
    Assert.IsTrue(detail.IsOpen());
    Assert.AreEqual(1, canvasGroup.alpha);

    closeAnimated.Invoke(detail, null);

    Assert.IsFalse(detail.IsOpen(), "A closing detail must stop accepting further escape handling immediately.");
    Assert.IsTrue(panel.gameObject.activeSelf, "The panel remains visible while its fade-out runs.");
    Assert.IsFalse(canvasGroup.interactable);
    Assert.IsFalse(canvasGroup.blocksRaycasts);

    float deadline = Time.realtimeSinceStartup + 1f;
    while (panel.gameObject.activeSelf && canvasGroup.alpha >= 1 && Time.realtimeSinceStartup < deadline)
    {
      yield return null;
    }
    Assert.Less(canvasGroup.alpha, 1, "The close action must fade the panel instead of hiding it instantly.");

    deadline = Time.realtimeSinceStartup + 1f;
    while (panel.gameObject.activeSelf && Time.realtimeSinceStartup < deadline)
    {
      yield return null;
    }
    Assert.IsFalse(panel.gameObject.activeSelf, "The panel must deactivate after its fade-out completes.");
  }

  [UnityTest]
  public IEnumerator ReopeningDuringFadeCancelsThePendingHide()
  {
    CreateDetail();
    detail.Open(0);
    closeAnimated.Invoke(detail, null);
    Assert.IsFalse(detail.IsOpen());

    detail.Open(0);
    Assert.IsTrue(detail.IsOpen());
    Assert.AreEqual(1, canvasGroup.alpha);
    Assert.IsTrue(canvasGroup.interactable);
    Assert.IsTrue(canvasGroup.blocksRaycasts);

    yield return null;
    Assert.IsTrue(detail.IsOpen(), "A canceled fade-out must not deactivate a newly reopened panel.");
    Assert.IsTrue(panel.gameObject.activeSelf);
  }

  [UnityTest]
  public IEnumerator ProgrammaticCloseRemainsImmediate()
  {
    CreateDetail();
    detail.Open(0);
    detail.Close();

    Assert.IsFalse(detail.IsOpen());
    Assert.IsFalse(panel.gameObject.activeSelf);
    yield return null;
  }
}
#endif
