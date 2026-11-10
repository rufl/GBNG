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

#if UNITY_EDITOR

using UnityEngine;
using UnityEngine.TestTools;
using NUnit.Framework;
using System.Collections;
using System.Collections.Generic;
using Behaviors;
using static BehaviorSystem;

public class BehaviorsTest
{
  private BehaviorSystem behaviorSystem;
  private VoosEngine voosEngine;

  public IEnumerator Setup()
  {
    TestUtil.TestScene scene = new TestUtil.TestScene("main");
    yield return scene.LoadAndWait();

    behaviorSystem = scene.FindRootComponent<BehaviorSystem>("ScriptingSystems");
    voosEngine = scene.FindRootComponent<VoosEngine>("ScriptingSystems");

    // Wait for loading done..
    while (true)
    {
      if (voosEngine.GetIsRunning())
      {
        yield break;
      }
      yield return null;
    }
  }

  struct TestUse
  {
    public string brainId;
    public string behaviorUri;
  }

  void PutTestUse(TestUse test)
  {
    var use = new BehaviorUse { behaviorUri = test.behaviorUri, id = NewGUID() };
    var brain = new Brain { behaviorUses = new BehaviorUse[] { use } };
    behaviorSystem.PutBrain(test.brainId, brain);
  }

  string NewGUID()
  {
    return behaviorSystem.GenerateUniqueId();
  }

  [UnityTest]
  public IEnumerator TestBasic()
  {
    yield return Setup();

    VoosActor actor = voosEngine.CreateActor(new Vector3(1, 2, 3), Quaternion.identity, _ => { });
    Assert.NotNull(actor);
    Assert.AreEqual(1.0, actor.GetTint().r, 1e-4);

    string brainId = actor.GetBrainName();
    string behaviorId = NewGUID();
    string js = @"
        export function onTick(api) {
          setTint(0.12, 0.34, 0.56);
        }
    ";
    behaviorSystem.PutBehavior(behaviorId, new Behavior { javascript = js });
    var use = new BehaviorUse
    {
      id = NewGUID(),
      behaviorUri = IdToEmbeddedBehaviorUri(behaviorId)
    };
    var brain = new Brain { behaviorUses = new BehaviorUse[] { use } };
    behaviorSystem.PutBrain(brainId, brain);

    // Let it run at least one voos update
    yield return new WaitForEndOfFrame();
    yield return new WaitForEndOfFrame();

    Assert.AreEqual(0.12, actor.GetTint().r, 1e-4);
  }


  [UnityTest]
  public IEnumerator TestHandleMultipleInOneFrame()
  {
    yield return Setup();

    VoosActor actor = voosEngine.CreateActor(new Vector3(1, 2, 3), Quaternion.identity, newActor => { });
    actor.SetTint(new Color(0.1f, 0.1f, 0.1f));
    Assert.NotNull(actor);
    Assert.AreEqual(1.0, actor.transform.position.x, 1e-4);

    string brainId = actor.GetBrainName();
    string behaviorId = behaviorSystem.GenerateUniqueId();
    string js = @"
    export function onMoveRight() {
      const c = getTint();
      c.r += 0.1;
      setTint(c.r, c.g, c.b);
    }
    ";
    behaviorSystem.PutBehavior(behaviorId, new Behaviors.Behavior { javascript = js });
    PutTestUse(new TestUse
    {
      behaviorUri = IdToEmbeddedBehaviorUri(behaviorId),
      brainId = brainId
    });

    // We expect x to increment by 2..
    voosEngine.EnqueueMessage(new VoosEngine.ActorMessage { name = "MoveRight", targetActor = actor.GetName() });
    voosEngine.EnqueueMessage(new VoosEngine.ActorMessage { name = "MoveRight", targetActor = actor.GetName() });

    // Let it run at least one voos update
    yield return new WaitForEndOfFrame();
    yield return new WaitForEndOfFrame();

    Assert.AreEqual(0.3, actor.GetTint().r, 1e-4);
  }

  [UnityTest]
  public IEnumerator TestClonePreservesChildSpawnTransform()
  {
    LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex(
      @"UnauthorizedAccessException: Access to the path '.*GameBuilderUserData\\Games' is denied\."));
    yield return Setup();

    Vector3 rootSpawnPosition = new Vector3(10f, 0f, 5f);
    Quaternion rootSpawnRotation = Quaternion.Euler(0f, 90f, 0f);
    Vector3 childLocalSpawnPosition = new Vector3(2f, 0f, 3f);
    Quaternion childLocalSpawnRotation = Quaternion.Euler(0f, 45f, 0f);
    VoosActor root = voosEngine.CreateActor(rootSpawnPosition, rootSpawnRotation, _ => { });
    Vector3 childSpawnPosition = rootSpawnPosition + rootSpawnRotation * childLocalSpawnPosition;
    Quaternion childSpawnRotation = rootSpawnRotation * childLocalSpawnRotation;
    VoosActor child = voosEngine.CreateActor(childSpawnPosition, childSpawnRotation, _ => { });
    child.transform.SetParent(root.transform, true);
    child.SetTransformParent(root.GetName());
    child.SetSpawnTransformParent(root.GetName());

    string behaviorId = NewGUID();
    behaviorSystem.PutBehavior(behaviorId, new Behavior
    {
      javascript = @"
        export function OnCloneTest(api) {
          api.clone(
            api.getActor().getName(),
            new THREE.Vector3(100, 0, 50),
            new THREE.Quaternion(0, 1, 0, 0));
        }"
    });
    PutTestUse(new TestUse
    {
      behaviorUri = IdToEmbeddedBehaviorUri(behaviorId),
      brainId = root.GetBrainName()
    });

    voosEngine.EnqueueMessage(new VoosEngine.ActorMessage { name = "CloneTest", targetActor = root.GetName() });
    yield return null;
    yield return null;
    yield return null;
    yield return null;

    VoosActor rootClone = null;
    VoosActor childClone = null;
    foreach (VoosActor actor in voosEngine.EnumerateActors())
    {
      if (actor.GetCloneParent() == root.GetName()) rootClone = actor;
      if (actor.GetCloneParent() == child.GetName()) childClone = actor;
    }
    Assert.IsNotNull(rootClone, "Script cloning must create the requested root actor.");
    Assert.IsNotNull(childClone, "Script cloning must create the root actor's child.");

    Quaternion requestedRootRotation = new Quaternion(0f, 1f, 0f, 0f);
    Vector3 requestedRootPosition = new Vector3(100f, 0f, 50f);
    Quaternion spawnRotationDelta = requestedRootRotation * Quaternion.Inverse(rootSpawnRotation);
    Vector3 expectedChildSpawnPosition = requestedRootPosition +
      spawnRotationDelta * (childSpawnPosition - rootSpawnPosition);
    Quaternion expectedChildSpawnRotation = spawnRotationDelta * childSpawnRotation;
    Assert.Less(Vector3.Distance(requestedRootPosition, rootClone.GetSpawnPosition()), 0.001f);
    Assert.Less(Quaternion.Angle(requestedRootRotation, rootClone.GetSpawnRotation()), 0.01f);
    Assert.Less(Vector3.Distance(expectedChildSpawnPosition, childClone.GetSpawnPosition()), 0.001f);
    Assert.Less(Quaternion.Angle(expectedChildSpawnRotation, childClone.GetSpawnRotation()), 0.01f);
  }

  [UnityTest]
  public IEnumerator TestCooldownSurvivesActorMemoryTransfer()
  {
    string originalUserDataDirName = Util.UserDataDirName;
    Util.UserDataDirName = System.IO.Path.Combine(
      "GAMEBUILDER", "Library", "BehaviorsTest-" + System.Guid.NewGuid().ToString("N"));
    string userDataRoot = Util.GetUserDataDir();
    try
    {
      LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex(
        @"UnauthorizedAccessException: Access to the path '.*GameBuilderUserData\\Games' is denied\."));
      yield return Setup();

      VoosActor source = voosEngine.CreateActor(new Vector3(1, 2, 3), Quaternion.identity, _ => { });
      string brainId = source.GetBrainName();
      string behaviorId = behaviorSystem.GenerateUniqueId();
      behaviorSystem.PutBehavior(behaviorId, new Behavior
      {
        javascript = @"
          export function onCooldownTest() {
            setTint(0.12, 0.34, 0.56);
            cooldown(10);
          }"
      });
      PutTestUse(new TestUse
      {
        behaviorUri = IdToEmbeddedBehaviorUri(behaviorId),
        brainId = brainId
      });

      voosEngine.EnqueueMessage(new VoosEngine.ActorMessage { name = "CooldownTest", targetActor = source.GetName() });
      yield return null;
      yield return null;
      Assert.AreEqual(0.12, source.GetTint().r, 1e-4);

      string transferredMemory = source.GetMemoryJsonSlow();
      VoosActor recipient = voosEngine.CreateActor(new Vector3(4, 5, 6), Quaternion.identity, _ => { });
      recipient.SetBrainName(brainId);
      recipient.SetMemoryJson(transferredMemory);
      yield return null;
      yield return null;

      voosEngine.EnqueueMessage(new VoosEngine.ActorMessage { name = "CooldownTest", targetActor = recipient.GetName() });
      yield return null;
      yield return null;

      Assert.AreEqual(1.0, recipient.GetTint().r, 1e-4,
        "The transferred actor must retain an active behavior cooldown.");
    }
    finally
    {
      Util.UserDataDirName = originalUserDataDirName;
      if (System.IO.Directory.Exists(userDataRoot)) System.IO.Directory.Delete(userDataRoot, true);
    }
  }

  [Test]
  public void TestBehaviorDbGarbageCollect()
  {
    var db = new Behaviors.Database();

    db.behaviors.Set("A", new Behavior { javascript = "test();" });
    db.behaviors.Set("B", new Behavior { javascript = "test();" });
    db.behaviors.Set("C", new Behavior { javascript = "test();" });
    db.behaviors.Set("D", new Behavior { javascript = "test();" });

    db.brains.Set("b0", new Brain
    {
      behaviorUses = new BehaviorUse[]{
        new BehaviorUse{id="u00", behaviorUri="embedded:B"},
        new BehaviorUse{id="u01", behaviorUri="embedded:C"}
      }
    });

    db.brains.Set("b1", new Brain
    {
      behaviorUses = new BehaviorUse[]{
        new BehaviorUse{id="u10", behaviorUri="embedded:C"},
        new BehaviorUse{id="u11", behaviorUri="embedded:D"}
      }
    });

    var usedBrainIds = new HashSet<string> { "b0" };

    db.GarbageCollect(false, usedBrainIds);

    Assert.IsTrue(db.brains.Exists("b0"), "Used brain not deleted");
    Assert.IsFalse(db.brains.Exists("b1"), "Unused brain is deleted");

    // All behaviors left alone
    Assert.IsTrue(db.behaviors.Exists("A"), "Unused behavior not deleted");
    Assert.IsTrue(db.behaviors.Exists("B"), "Unused behavior not deleted");
    Assert.IsTrue(db.behaviors.Exists("C"), "Unused behavior not deleted");
    Assert.IsTrue(db.behaviors.Exists("D"), "Unused behavior not deleted");

  }

  [Test]
  public void TestBehaviorDbGarbageCollectDeleteUnusedBehaviors()
  {
    var db = new Behaviors.Database();

    db.behaviors.Set("A", new Behaviors.Behavior { javascript = "test();" });
    db.behaviors.Set("B", new Behaviors.Behavior { javascript = "test();" });
    db.behaviors.Set("C", new Behaviors.Behavior { javascript = "test();" });
    db.behaviors.Set("D", new Behaviors.Behavior { javascript = "test();" });

    db.brains.Set("b0", new Brain
    {
      behaviorUses = new BehaviorUse[]{
        new BehaviorUse{id="u00", behaviorUri="embedded:B"},
        new BehaviorUse{id="u01", behaviorUri="embedded:C"}
      }
    });

    db.brains.Set("b1", new Brain
    {
      behaviorUses = new BehaviorUse[]{
        new BehaviorUse{id="u10", behaviorUri="embedded:C"},
        new BehaviorUse{id="u11", behaviorUri="embedded:D"}
      }
    });

    var usedBrainIds = new HashSet<string> { "b0" };

    db.GarbageCollect(true, usedBrainIds);

    Assert.IsTrue(db.brains.Exists("b0"), "Used brain not deleted");

    Assert.IsFalse(db.brains.Exists("b1"), "Unused brain is deleted");

    // Only used behaviors left..
    Assert.IsFalse(db.behaviors.Exists("A"));
    Assert.IsTrue(db.behaviors.Exists("B"));
    Assert.IsTrue(db.behaviors.Exists("C"));
    Assert.IsFalse(db.behaviors.Exists("D"));

  }

}

#endif
