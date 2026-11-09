/*
 * Copyright 2019 Google LLC
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     https://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Behaviors;
using System.IO;
using BehaviorProperties;

public partial class BehaviorCards
{
  class MiscPanel : CardPanel.IAssignedPanel, IDeckModel, Unassigner
  {
    ActorBehaviorsEditor actorEditor;

    public MiscPanel(ActorBehaviorsEditor actorEditor)
    {
      this.actorEditor = actorEditor;
    }

    List<AssignedBehavior> GetCustomCardUses()
    {
      List<AssignedBehavior> cards = new List<AssignedBehavior>();
      foreach (var useEditor in actorEditor.GetAssignedBehaviors())
      {
        var behaviorData = useEditor.GetBehaviorData();
        if (!CardMetadata.IsCardOfCategory(behaviorData, CUSTOM_CATEGORY) ||
            useEditor.GetBehaviorUri() == "builtin:Default Behavior")
        {
          continue;
        }
        cards.Add(useEditor);
      }
      return cards;
    }

    List<AssignedBehavior> GetOrderedCustomCardUses()
    {
      List<AssignedBehavior> assignedCards = GetCustomCardUses();
      Dictionary<string, AssignedBehavior> usesById = new Dictionary<string, AssignedBehavior>();
      List<string> assignedUseIds = new List<string>(assignedCards.Count);
      foreach (AssignedBehavior assigned in assignedCards)
      {
        usesById[assigned.useId] = assigned;
        assignedUseIds.Add(assigned.useId);
      }

      BrainMetadata metadata = Util.FromJsonSafe<BrainMetadata>(actorEditor.GetMetadataJson());
      List<string> orderedUseIds = MergeCustomCardUseOrder(metadata.miscPanelCardUseIds, assignedUseIds);
      List<AssignedBehavior> orderedCards = new List<AssignedBehavior>(orderedUseIds.Count);
      foreach (string useId in orderedUseIds)
      {
        AssignedBehavior assigned;
        if (usesById.TryGetValue(useId, out assigned))
        {
          orderedCards.Add(assigned);
        }
      }
      return orderedCards;
    }

    void SaveCustomCardOrder(List<string> orderedUseIds)
    {
      BrainMetadata metadata = Util.FromJsonSafe<BrainMetadata>(actorEditor.GetMetadataJson());
      metadata.miscPanelCardUseIds = orderedUseIds.ToArray();
      actorEditor.SetMetadataJson(JsonUtility.ToJson(metadata));
    }

    public string GetDescription() { return ""; }
    public Color GetColor() { return CUSTOM_PANEL_COLOR; }
    public string GetPropertyName() { return null; } // For CardSlot.IModel
    public Sprite GetIcon()
    {
      return Resources.Load("BuiltinAssets/PanelIcons/gear-icon", typeof(Sprite)) as Sprite;
    }
    public string GetTitle() { return "Custom"; }

    public IEnumerable<IDeckModel> GetDecks()
    {
      // We just have one slot, that we implement ourselves.
      yield return this;
    }

    public IEnumerable<ICardAssignmentModel> GetAssignedCards()
    {
      foreach (AssignedBehavior useEditor in GetOrderedCustomCardUses())
      {
        yield return new CardAssignment(useEditor, this);
      }
    }

    // The one slot we have
    public string GetCardCategory() { return CUSTOM_CATEGORY; }
    public string GetPrompt() { return ""; }

    public ICardAssignmentModel OnAssignCard(ICardModel card, int index = -1)
    {
      using (this.actorEditor.StartUndo($"Add Custom card {card.GetTitle()}"))
      {
        var assigned = actorEditor.AddBehavior(((UnassignedCard)card).GetUnassignedBehaviorItem());
        List<string> order = GetOrderedCustomCardUses().Select(use => use.useId).ToList();
        InsertCustomCardUseId(order, assigned.useId, index);
        SaveCustomCardOrder(order);
        return new CardAssignment(assigned, this);
      }
    }

    public void Remove()
    {
      Util.LogError($"The default panel cannot be removed.");
    }

    public void Unassign(CardAssignment card)
    {
      using (this.actorEditor.StartUndo($"Remove card {card.GetCard().GetTitle()}"))
      {
        List<string> order = GetOrderedCustomCardUses().Select(use => use.useId).ToList();
        order.Remove(card.GetId());
        card.GetAssignedBehavior().RemoveSelfFromActor();
        SaveCustomCardOrder(order);
      }
    }

    public ICardAssignmentModel CreateAndAssignNewCard()
    {
      string metaJson = JsonUtility.ToJson(new CardMetadata
      {
        cardSystemCardData = new CardMetadata.Data
        {
          description = "Describe what your card does here!",
          isCard = true,
          title = "Name your card",
          categories = CUSTOM_CATEGORIES
        }
      });
      UnassignedBehavior newBehavior = this.actorEditor.CreateNewBehavior(CodeTemplates.MISC, metaJson);
      return this.OnAssignCard(new UnassignedCard(newBehavior));
    }

    public int GetNumAssignedCards()
    {
      return GetOrderedCustomCardUses().Count;
    }

    public IEnumerable<ICardModel> GetDefaultCards()
    {
      yield break;
    }

    public AssignedBehavior GetBehavior()
    {
      return null;
    }

    public int GetIndexOf(ICardAssignmentModel assignedModel)
    {
      if (assignedModel == null) return -1;
      string useId = assignedModel.GetId();
      List<AssignedBehavior> cards = GetOrderedCustomCardUses();
      for (int index = 0; index < cards.Count; index++)
      {
        if (cards[index].useId == useId) return index;
      }
      return -1;
    }
    public string GetId()
    {
      return BehaviorCards.GetMiscPanelId();
    }

    public bool IsFake()
    {
      return true;
    }

    public CardPanel.PanelUse GetUse()
    {
      return Util.FromJsonSafe<BrainMetadata>(actorEditor.GetMetadataJson()).miscPanelUseMetadata;
    }

    public void SetUse(CardPanel.PanelUse miscMeta, string undoLabel)
    {
      using (undoLabel != null ?
        actorEditor.StartUndo(undoLabel) :
        new Util.DummyDisposable())
      {
        BrainMetadata md = Util.FromJsonSafe<BrainMetadata>(actorEditor.GetMetadataJson());
        md.miscPanelUseMetadata = miscMeta;
        actorEditor.SetMetadataJson(JsonUtility.ToJson(md));
      }
    }

    public CardPanel.IAssignedPanel Duplicate()
    {
      throw new NotSupportedException("The built-in Custom panel cannot be duplicated.");
    }
  }
  static List<string> MergeCustomCardUseOrder(
    IEnumerable<string> savedUseIds,
    IEnumerable<string> assignedUseIds)
  {
    List<string> currentOrder = assignedUseIds == null
      ? new List<string>()
      : new List<string>(assignedUseIds);
    HashSet<string> availableUseIds = new HashSet<string>();
    foreach (string useId in currentOrder)
    {
      if (!string.IsNullOrEmpty(useId)) availableUseIds.Add(useId);
    }

    List<string> mergedOrder = new List<string>(currentOrder.Count);
    HashSet<string> addedUseIds = new HashSet<string>();
    if (savedUseIds != null)
    {
      foreach (string useId in savedUseIds)
      {
        if (!string.IsNullOrEmpty(useId) &&
            availableUseIds.Contains(useId) &&
            addedUseIds.Add(useId))
        {
          mergedOrder.Add(useId);
        }
      }
    }

    foreach (string useId in currentOrder)
    {
      if (!string.IsNullOrEmpty(useId) && addedUseIds.Add(useId))
      {
        mergedOrder.Add(useId);
      }
    }
    return mergedOrder;
  }

  static void InsertCustomCardUseId(List<string> orderedUseIds, string useId, int index)
  {
    orderedUseIds.Remove(useId);
    int insertIndex = index < 0 ? orderedUseIds.Count : Mathf.Clamp(index, 0, orderedUseIds.Count);
    orderedUseIds.Insert(insertIndex, useId);
  }

  public static string CUSTOM_CATEGORY = "Custom";
  public static string[] CUSTOM_CATEGORIES = new string[] { CUSTOM_CATEGORY };
  public static Color CUSTOM_PANEL_COLOR = Color.grey;

  public static string GetMiscPanelId()
  {
    return "__MISC__";
  }

  public static string GetMiscMetadataJson()
  {
    return JsonUtility.ToJson(new CardMetadata
    {
      cardSystemCardData = new CardMetadata.Data
      {
        description = "Describe what your card does here!",
        isCard = true,
        title = "Name your card",
        categories = CUSTOM_CATEGORIES
      }
    });
  }
}