#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

public class ExampleGameTemplateTests
{
  [Test]
  public void DrivingTemplateAssignsEachCarToADistinctPlayerSlot()
  {
    string path = Path.Combine(Application.streamingAssetsPath, "ExampleGames", "Public", "template-driving.voos");
    SaveLoadController.SaveGame save = SaveLoadController.ReadSaveGame(path);
    var assignedPlayerNumbers = new HashSet<int>();
    int carCount = 0;

    foreach (VoosActor.PersistedState actor in save.voosEngineState.actors)
    {
      if (actor.renderableUri != "builtin:BuiltinAssets/City/SmartCar") continue;
      carCount++;

      int brainIndex = Array.IndexOf(save.behaviorDatabase.brainIds, actor.brainName);
      Assert.That(brainIndex, Is.GreaterThanOrEqualTo(0), "Missing brain for " + actor.displayName);
      Behaviors.Brain brain = save.behaviorDatabase.brains[brainIndex];
      Behaviors.BehaviorUse controlsPanel = null;
      foreach (Behaviors.BehaviorUse use in brain.behaviorUses)
      {
        if (use.behaviorUri == "builtin:Player Controls Panel v2")
        {
          controlsPanel = use;
          break;
        }
      }

      Assert.IsNotNull(controlsPanel, "Missing player controls for " + actor.displayName);
      int playerNumber = controlsPanel.GetPropertyValue<int>("PlayerNumber");
      Assert.That(playerNumber, Is.InRange(1, 4), actor.displayName + " must be assigned to Player 1-4");
      Assert.IsTrue(assignedPlayerNumbers.Add(playerNumber), "Multiple cars are assigned to Player " + playerNumber);
    }

    Assert.AreEqual(4, carCount, "The driving template should include the four cars shown in its thumbnail.");
    CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4 }, assignedPlayerNumbers);
  }
}
#endif
