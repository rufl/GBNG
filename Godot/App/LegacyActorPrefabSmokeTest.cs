using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GBNG.Behaviors;
using GBNG.Domain;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunActorPrefabSmoke()
    {
        int exitCode = 0;
        string? originalProjectPath = _document?.SourcePath;
        bool originalPlayMode = _playMode;
        string? temporaryProjectPath = null;
        try
        {
            await WaitForUiFrames(2);
            LegacySaveDocument document = _document
                ?? throw new InvalidOperationException("Open a .voos project before running the actor-prefab smoke.");
            if (originalProjectPath is null)
                throw new InvalidOperationException("The actor-prefab smoke project has no source path.");
            LegacyBehaviorRuntime runtime = _behaviorRuntime
                ?? throw new InvalidOperationException("The actor-prefab smoke requires the behavior runtime.");
            if (!_playMode)
                SetPlayMode(true);
            if (!_playMode)
                throw new InvalidOperationException("Play mode could not start for the actor-prefab smoke.");

            WorldActorNode template = _actors.FirstOrDefault(actor =>
                    actor.GetNodeOrNull<Node3D>("Renderable") is not null
                    && !actor.LegacyName.StartsWith("builtin:", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The smoke project has no local actor with a renderable.");

            const string sourceRootName = "prefab-smoke-source-root";
            const string sourceChildName = "prefab-smoke-source-child";
            const string sourceBrainId = "prefab-smoke-source-brain";
            string dependencyId = Guid.NewGuid().ToString("N");
            string prefabSojoId = $"c78581254ff845fc9383f170201cd4d8-{Guid.NewGuid():N}";
            const string dependencyName = "Prefab smoke sound dependency";
            const string sourceDependencyContent = "{\"effectType\":0,\"marker\":\"prefab\"}";

            JsonObject rootData = (JsonObject)template.State.Data.DeepClone();
            rootData["name"] = sourceRootName;
            rootData["displayName"] = "Prefab smoke root";
            rootData["brainName"] = sourceBrainId;
            rootData["transformParent"] = "";
            rootData["spawnTransformParent"] = "";
            rootData["sfxId"] = dependencyId;
            var rootState = new LegacyActorData(rootData);
            rootState.SetWorldTransform(new Transform3D(Basis.Identity, new Vector3(0f, 1f, 0f)));
            rootState.WriteLegacyVector("spawnPosition", CoordinateSpace.ToLegacy(new Vector3(0f, 1f, 0f)));
            rootState.WriteLegacyQuaternion("spawnRotation", CoordinateSpace.ToLegacy(Quaternion.Identity));

            JsonObject childData = (JsonObject)rootData.DeepClone();
            childData["name"] = sourceChildName;
            childData["displayName"] = "Prefab smoke child";
            childData["transformParent"] = sourceRootName;
            childData["spawnTransformParent"] = sourceRootName;
            childData["sfxId"] = "";
            var childState = new LegacyActorData(childData);
            childState.SetWorldTransform(new Transform3D(Basis.Identity, new Vector3(2f, 1f, 0f)));
            childState.WriteLegacyVector("spawnPosition", CoordinateSpace.ToLegacy(new Vector3(2f, 1f, 0f)));
            childState.WriteLegacyQuaternion("spawnRotation", CoordinateSpace.ToLegacy(Quaternion.Identity));

            var dependency = new JsonObject
            {
                ["id"] = dependencyId,
                ["name"] = dependencyName,
                ["contentType"] = "SoundEffect",
                ["content"] = sourceDependencyContent
            };
            var brain = new JsonObject
            {
                ["behaviorUses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["behaviorUri"] = "builtin:Default Behavior",
                        ["propertyAssignments"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["propertyName"] = "targetActor",
                                ["valueJson"] = "{\"value\":\"prefab-smoke-source-child\"}"
                            }
                        }
                    }
                }
            };
            var prefab = new JsonObject
            {
                ["version"] = 2,
                ["label"] = "Prefab smoke hierarchy",
                ["actors"] = new JsonArray(rootData, childData),
                ["brainDatabase"] = new JsonObject
                {
                    ["version"] = 4,
                    ["behaviorIds"] = new JsonArray(),
                    ["behaviors"] = new JsonArray(),
                    ["brainIds"] = new JsonArray(JsonValue.Create(sourceBrainId)),
                    ["brains"] = new JsonArray(brain)
                },
                ["sojos"] = new JsonArray((JsonObject)dependency.DeepClone())
            };

            JsonObject database = LegacySaveDocument.EnsureObject(document.Root, "sojoDatabase");
            JsonArray sojos = database["sojos"] as JsonArray ?? (JsonArray)(database["sojos"] = new JsonArray())!;
            sojos.Add(new JsonObject
            {
                ["id"] = dependencyId,
                ["name"] = dependencyName,
                ["contentType"] = "SoundEffect",
                ["content"] = "{\"effectType\":0,\"marker\":\"existing conflict\"}"
            });
            sojos.Add(new JsonObject
            {
                ["id"] = prefabSojoId,
                ["name"] = "Prefab smoke hierarchy",
                ["contentType"] = "ActorPrefab",
                ["content"] = prefab.ToJsonString()
            });

            int actorCountBefore = _actors.Count;
            Vector3 targetLegacyPosition = new(8f, 4f, -6f);
            JsonObject firstResult = ParsePrefabResult(runtime.InstantiatePrefab(CreatePrefabRequest(prefabSojoId, targetLegacyPosition)));
            string firstRootName = LegacySaveDocument.ReadString(firstResult["name"]);
            WorldActorNode firstRoot = FindActor(firstRootName)
                ?? throw new InvalidOperationException("The first actor-prefab root was not created.");
            WorldActorNode firstChild = _actors.FirstOrDefault(actor => actor.State.TransformParent == firstRootName)
                ?? throw new InvalidOperationException("The saved actor hierarchy did not instantiate its child.");
            string firstBrainId = firstRoot.State.BrainName;
            string importedDependencyId = LegacySaveDocument.ReadString(firstRoot.State.Data["sfxId"]);
            if (LegacySaveDocument.ReadInt(firstResult["actorId"], -1) != actorCountBefore)
                throw new InvalidOperationException($"The prefab returned actor ID {LegacySaveDocument.ReadInt(firstResult["actorId"], -1)}, expected {actorCountBefore}.");
            if (firstRoot.State.DisplayName != "Prefab smoke root-Script-Instance")
                throw new InvalidOperationException($"The prefab root label was '{firstRoot.State.DisplayName}'.");
            if (!firstRoot.GlobalPosition.IsEqualApprox(CoordinateSpace.ToGodot(targetLegacyPosition)))
                throw new InvalidOperationException($"The prefab root spawned at {firstRoot.GlobalPosition}, expected {CoordinateSpace.ToGodot(targetLegacyPosition)}.");
            if (!firstChild.GlobalPosition.IsEqualApprox(CoordinateSpace.ToGodot(targetLegacyPosition) + Vector3.Right * 2f))
                throw new InvalidOperationException($"The prefab child spawned at {firstChild.GlobalPosition}, expected {CoordinateSpace.ToGodot(targetLegacyPosition) + Vector3.Right * 2f}.");
            if (firstChild.GetParent() != firstRoot || !firstChild.State.WasClonedByScript)
                throw new InvalidOperationException("The prefab child lost its parent or script-clone state.");
            if (firstBrainId.Length == 0 || firstBrainId == sourceBrainId || firstChild.State.BrainName != firstBrainId)
                throw new InvalidOperationException($"The prefab actors did not receive an independent shared brain: root={firstBrainId}, child={firstChild.State.BrainName}.");

            JsonObject importedBrain = FindPrefabBrain(document, firstBrainId);
            JsonArray importedUses = importedBrain["behaviorUses"] as JsonArray
                ?? throw new InvalidOperationException("The imported prefab brain lost its behavior uses.");
            JsonArray assignments = importedUses[0]?["propertyAssignments"] as JsonArray
                ?? throw new InvalidOperationException("The imported prefab brain lost its actor reference assignment.");
            string importedReferenceJson = LegacySaveDocument.ReadString(assignments[0]?["valueJson"]);
            string importedActorReference = LegacySaveDocument.ReadString(JsonNode.Parse(importedReferenceJson)?["value"]);
            if (importedActorReference != firstChild.LegacyName)
                throw new InvalidOperationException("The actor reference inside the copied brain was not remapped to the new child.");
            JsonObject importedDependency = sojos.OfType<JsonObject>()
                .FirstOrDefault(sojo => LegacySaveDocument.ReadString(sojo["id"]) == importedDependencyId)
                ?? throw new InvalidOperationException("The prefab's Sojo dependency was not imported.");
            if (importedDependencyId == dependencyId
                || LegacySaveDocument.ReadString(importedDependency["content"]) != sourceDependencyContent)
                throw new InvalidOperationException("A conflicting Sojo ID was not remapped to the prefab's dependency.");

            JsonObject secondResult = ParsePrefabResult(runtime.InstantiatePrefab(CreatePrefabRequest(prefabSojoId, targetLegacyPosition + new Vector3(5f, 0f, 0f))));
            string secondRootName = LegacySaveDocument.ReadString(secondResult["name"]);
            WorldActorNode secondRoot = FindActor(secondRootName)
                ?? throw new InvalidOperationException("The second actor-prefab root was not created.");
            WorldActorNode secondChild = _actors.FirstOrDefault(actor => actor.State.TransformParent == secondRootName)
                ?? throw new InvalidOperationException("The second prefab instance did not recreate its child.");
            int importedDependencyCopies = sojos.OfType<JsonObject>().Count(sojo =>
                LegacySaveDocument.ReadString(sojo["name"]) == dependencyName
                && LegacySaveDocument.ReadString(sojo["content"]) == sourceDependencyContent);
            if (secondRootName == firstRootName
                || secondRoot.State.BrainName == firstBrainId
                || secondChild.State.BrainName != secondRoot.State.BrainName
                || LegacySaveDocument.ReadString(secondRoot.State.Data["sfxId"]) != importedDependencyId
                || importedDependencyCopies != 1)
                throw new InvalidOperationException("Repeated prefab placement reused actor or brain IDs, or duplicated an already-imported Sojo dependency.");
            int builtinActorIndex = _actors.Count;
            Vector3 builtinLegacyPosition = new(-4f, 2f, 9f);
            JsonObject builtinResult = ParsePrefabResult(runtime.InstantiatePrefab(CreatePrefabRequest("builtin:Empty", builtinLegacyPosition)));
            string builtinName = LegacySaveDocument.ReadString(builtinResult["name"]);
            WorldActorNode builtinActor = FindActor(builtinName)
                ?? throw new InvalidOperationException("The legacy actorData prefab did not instantiate.");
            if (LegacySaveDocument.ReadInt(builtinResult["actorId"], -1) != builtinActorIndex
                || builtinActor.State.DisplayName != "Empty-Script-Instance"
                || builtinActor.RenderableUri != "builtin:BuiltinAssets/Empty"
                || !builtinActor.GlobalPosition.IsEqualApprox(CoordinateSpace.ToGodot(builtinLegacyPosition)))
                throw new InvalidOperationException("The legacy single-actor actorData prefab did not preserve its identity, renderable, or spawn transform.");


            temporaryProjectPath = Path.Combine(
                ProjectSettings.GlobalizePath("user://"),
                $"actor-prefab-smoke-{Guid.NewGuid():N}.voos");
            document.SaveCopyTo(temporaryProjectPath);
            LegacySaveDocument reloaded = LegacySaveDocument.Load(temporaryProjectPath);
            LegacyActorData persistedRoot = reloaded.Actors.FirstOrDefault(actor => actor.Name == firstRootName)
                ?? throw new InvalidOperationException("The first prefab instance did not survive a .voos save and reload.");
            if (persistedRoot.BrainName != firstBrainId
                || reloaded.Actors.FirstOrDefault(actor => actor.Name == firstChild.LegacyName)?.TransformParent != firstRootName
                || !reloaded.Root["sojoDatabase"]!["sojos"]!.AsArray().OfType<JsonObject>().Any(sojo =>
                    LegacySaveDocument.ReadString(sojo["id"]) == importedDependencyId))
                throw new InvalidOperationException("The prefab actors, hierarchy, brain, or imported Sojo dependency did not persist.");

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/actor-prefab-smoke.png");
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            if (GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the actor-prefab smoke screenshot.");
            GD.Print($"ACTOR_PREFAB_SMOKE_OK customSojo=true hierarchy=2 brainClone=true actorReferences=true sojoConflictRemap=true repeatedPlacement=true persistence=true screenshot={screenshot}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ACTOR_PREFAB_SMOKE_FAILED: {exception}");
        }
        finally
        {
            try
            {
                if (!originalPlayMode && _playMode)
                    SetPlayMode(false);
                if (originalProjectPath is not null)
                {
                    LoadProject(originalProjectPath);
                    await WaitForUiFrames(2);
                }
            }
            catch (Exception exception)
            {
                exitCode = 1;
                GD.PushError($"ACTOR_PREFAB_SMOKE_CLEANUP_FAILED: {exception}");
            }
            if (temporaryProjectPath is not null && File.Exists(temporaryProjectPath))
                File.Delete(temporaryProjectPath);
        }

        GetTree().Quit(exitCode);
    }

    private static JsonObject CreatePrefabRequest(string prefabId, Vector3 legacyPosition)
        => new()
        {
            ["prefabUri"] = prefabId,
            ["creatorName"] = "Actor prefab smoke",
            ["position"] = new JsonObject { ["x"] = legacyPosition.X, ["y"] = legacyPosition.Y, ["z"] = legacyPosition.Z },
            ["rotation"] = new JsonObject { ["x"] = 0f, ["y"] = 0f, ["z"] = 0f, ["w"] = 1f }
        };

    private static JsonObject ParsePrefabResult(string result)
        => JsonNode.Parse(result) as JsonObject
            ?? throw new InvalidOperationException("The actor-prefab runtime returned an invalid result.");


    private static JsonObject FindPrefabBrain(LegacySaveDocument document, string brainId)
    {
        JsonObject database = document.Root["behaviorDatabase"] as JsonObject
            ?? throw new InvalidOperationException("The actor-prefab smoke project has no behavior database.");
        JsonArray ids = database["brainIds"] as JsonArray
            ?? throw new InvalidOperationException("The actor-prefab smoke project has no brain IDs.");
        JsonArray brains = database["brains"] as JsonArray
            ?? throw new InvalidOperationException("The actor-prefab smoke project has no brains.");
        for (int index = 0; index < Math.Min(ids.Count, brains.Count); index++)
        {
            if (LegacySaveDocument.ReadString(ids[index]) == brainId && brains[index] is JsonObject brain)
                return brain;
        }
        throw new InvalidOperationException($"The actor-prefab brain '{brainId}' was not merged into the project.");
    }
}
