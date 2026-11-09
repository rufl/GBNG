#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class BehaviorCardsMiscPanelTests
{
  static readonly BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

  [UnityTest]
  public IEnumerator CustomCardOrderPersistsWithoutReorderingRuntimeBehaviors()
  {
    TestUtil.TestScene scene = new TestUtil.TestScene("main");
    yield return scene.LoadAndWait();

    VoosEngine engine = scene.FindRootComponent<VoosEngine>("ScriptingSystems");
    while (!engine.GetIsRunning()) yield return null;

    VoosActor actor = engine.CreateActor(Vector3.zero, Quaternion.identity, _ => { });
    Assert.IsNotNull(actor);
    ActorBehaviorsEditor actorEditor = ActorBehaviorsEditor.FromActor(actor);

    GameObject cardsObject = new GameObject("BehaviorCardsMiscPanelTests");
    cardsObject.SetActive(false);
    try
    {
      BehaviorCards behaviorCards = cardsObject.AddComponent<BehaviorCards>();
      FieldInfo engineField = typeof(BehaviorCards).GetField("engine", InstanceFields);
      Assert.IsNotNull(engineField);
      engineField.SetValue(behaviorCards, engine);

      IDeckModel customDeck = GetCustomDeck(behaviorCards, actor);
      AssignedBehavior first = AddCustomCard(actorEditor);
      AssignedBehavior second = AddCustomCard(actorEditor);

      List<ICardAssignmentModel> legacyCards = new List<ICardAssignmentModel>(customDeck.GetAssignedCards());
      CollectionAssert.AreEqual(new[] { first.useId, second.useId }, legacyCards.Select(card => card.GetId()));
      Assert.AreEqual(2, customDeck.GetNumAssignedCards());
      Assert.IsEmpty(customDeck.GetDefaultCards());
      Assert.AreEqual(0, customDeck.GetIndexOf(legacyCards[0]));
      Assert.AreEqual(1, customDeck.GetIndexOf(legacyCards[1]));

      string[] runtimeOrderBeforeInsert = actorEditor.GetAssignedBehaviors().Select(use => use.useId).ToArray();
      BehaviorCards.UnassignedCard insertedCard = new BehaviorCards.UnassignedCard(
        actorEditor.CreateNewBehavior(CodeTemplates.MISC, BehaviorCards.GetMiscMetadataJson()));
      ICardAssignmentModel inserted = customDeck.OnAssignCard(insertedCard, 1);

      List<ICardAssignmentModel> orderedCards = new List<ICardAssignmentModel>(customDeck.GetAssignedCards());
      CollectionAssert.AreEqual(
        new[] { first.useId, inserted.GetId(), second.useId },
        orderedCards.Select(card => card.GetId()));
      CollectionAssert.AreEqual(
        runtimeOrderBeforeInsert.Concat(new[] { inserted.GetId() }),
        actorEditor.GetAssignedBehaviors().Select(use => use.useId));
      IDeckModel reloadedDeck = GetCustomDeck(behaviorCards, actor);
      CollectionAssert.AreEqual(
        new[] { first.useId, inserted.GetId(), second.useId },
        reloadedDeck.GetAssignedCards().Select(card => card.GetId()));
    }
    finally
    {
      engine.DestroyActor(actor);
      Object.DestroyImmediate(cardsObject);
    }

    yield return null;
  }

  static IDeckModel GetCustomDeck(BehaviorCards behaviorCards, VoosActor actor)
  {
    ICardManagerModel manager = behaviorCards.GetCardManager(actor);
    CardPanel.IAssignedPanel panel = manager.GetAssignedPanels()
      .Single(assignedPanel => assignedPanel.GetId() == BehaviorCards.GetMiscPanelId());
    return panel.GetDecks().Single();
  }

  static AssignedBehavior AddCustomCard(ActorBehaviorsEditor actorEditor)
  {
    UnassignedBehavior behavior = actorEditor.CreateNewBehavior(
      CodeTemplates.MISC,
      BehaviorCards.GetMiscMetadataJson());
    return actorEditor.AddBehavior(behavior);
  }
}
#endif
