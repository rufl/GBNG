using System.Text.Json;
using System.Text.Json.Nodes;
using GBNG.Assets;
using GBNG.Domain;
using GBNG.World;
using Godot;
using GBNG.Audio;

namespace GBNG.Behaviors;

public sealed partial class LegacyBehaviorRuntime
{
    private const uint ActorCollisionLayer = 1u;
    private const uint TerrainCollisionLayer = 2u;
    private const uint ActorTriggerCollisionLayer = 4u;
    private const string ActorPrefabRoot = "res://Assets/StreamingAssets/PrefabLibrary/ActorPrefabs";
    private readonly Dictionary<string, AudioStream> _soundCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _soundSpatialization = new(StringComparer.Ordinal);
    private bool _refreshDatabasePending;

    private string OverlapSphere(JsonObject args)
    {
        Vector3 center = CoordinateSpace.ToGodot(LegacyActorData.ReadVector(args["center"], Vector3.Zero));
        float radius = Math.Max(0f, LegacySaveDocument.ReadFloat(args["radius"], 0f));
        string tag = LegacySaveDocument.ReadString(args["tag"]);
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new SphereShape3D { Radius = radius },
            Transform = new Transform3D(Basis.Identity, center),
            CollisionMask = ActorCollisionLayer | ActorTriggerCollisionLayer,
            CollideWithBodies = true,
            CollideWithAreas = true
        };
        var names = new List<string>();
        foreach (Godot.Collections.Dictionary hit in _world.GetWorld3D().DirectSpaceState.IntersectShape(query, 500))
        {
            WorldActorNode? actor = FindActorFromHit(hit);
            if (actor is not null && (tag.Length == 0 || ActorHasTag(actor, tag)))
                names.Add(actor.LegacyName);
        }
        return JsonSerializer.Serialize(names);
    }

    private string CheckBox(JsonObject args)
    {
        if (args["box"] is not JsonObject box)
            return "false";
        Vector3 center = CoordinateSpace.ToGodot(LegacyActorData.ReadVector(box["center"], Vector3.Zero));
        Vector3 dimensions = LegacyActorData.ReadVector(box["dimensions"], Vector3.Zero);
        Quaternion rotation = CoordinateSpace.ToGodot(LegacyActorData.ReadQuaternion(box["rotation"]));
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new BoxShape3D { Size = new Vector3(Math.Abs(dimensions.X), Math.Abs(dimensions.Y), Math.Abs(dimensions.Z)) },
            Transform = new Transform3D(new Basis(rotation), center),
            CollisionMask = ActorCollisionLayer | TerrainCollisionLayer,
            CollideWithBodies = true,
            CollideWithAreas = false
        };
        return (_world.GetWorld3D().DirectSpaceState.IntersectShape(query, 1).Count > 0).ToString().ToLowerInvariant();
    }

    private string Cast(JsonObject args)
    {
        Vector3 origin = CoordinateSpace.ToGodot(LegacyActorData.ReadVector(args["origin"], Vector3.Zero));
        Vector3 direction = CoordinateSpace.ToGodot(LegacyActorData.ReadVector(args["dir"], Vector3.Forward));
        float maxDistance = Math.Max(0f, LegacySaveDocument.ReadFloat(args["maxDist"], 0f));
        float radius = Math.Max(0f, LegacySaveDocument.ReadFloat(args["radius"], 0f));
        int mode = LegacySaveDocument.ReadInt(args["mode"], 0);
        bool includeActors = LegacySaveDocument.ReadBool(args["includeActors"], true);
        bool includeTerrain = LegacySaveDocument.ReadBool(args["includeTerrain"], true);
        string excludeActor = LegacySaveDocument.ReadString(args["excludeActor"]);
        uint mask = (includeActors ? ActorCollisionLayer | ActorTriggerCollisionLayer : 0u) | (includeTerrain ? TerrainCollisionLayer : 0u);
        if (mask == 0u || direction.LengthSquared() == 0f || maxDistance == 0f)
            return mode is 2 or 3 ? "{\"hits\":[]}" : mode == 1 ? "null" : "false";
        direction = direction.Normalized();

        var hits = new List<LegacyCastHit>();
        var excluded = new Godot.Collections.Array<Rid>();
        WorldActorNode? excludedActorNode = FindActor(excludeActor);
        if (excludedActorNode is not null)
            excluded.Add(excludedActorNode.GetRid());
        PhysicsDirectSpaceState3D space = _world.GetWorld3D().DirectSpaceState;
        int remaining = 500;
        while (remaining-- > 0)
        {
            Godot.Collections.Dictionary hit;
            float distance;
            Vector3 point;
            Rid rid;
            if (radius < 0.01f)
            {
                var query = PhysicsRayQueryParameters3D.Create(origin, origin + direction * maxDistance);
                query.CollisionMask = mask;
                query.CollideWithAreas = true;
                query.CollideWithBodies = true;
                query.Exclude = excluded;
                hit = space.IntersectRay(query);
                if (hit.Count == 0)
                    break;
                point = hit["position"].AsVector3();
                distance = origin.DistanceTo(point);
                rid = hit["rid"].AsRid();
            }
            else
            {
                var query = new PhysicsShapeQueryParameters3D
                {
                    Shape = new SphereShape3D { Radius = radius },
                    Transform = new Transform3D(Basis.Identity, origin),
                    Motion = direction * maxDistance,
                    CollisionMask = mask,
                    CollideWithAreas = true,
                    CollideWithBodies = true,
                    Exclude = excluded
                };
                float[] fractions = space.CastMotion(query);
                if (fractions.Length < 2 || fractions[0] >= 1f)
                    break;
                distance = fractions[0] * maxDistance;
                query.Transform = new Transform3D(Basis.Identity, origin + direction * Math.Max(0f, distance - 0.001f));
                query.Motion = Vector3.Zero;
                hit = space.GetRestInfo(query);
                if (hit.Count == 0)
                    break;
                point = hit.ContainsKey("point") ? hit["point"].AsVector3() : origin + direction * distance;
                rid = hit["rid"].AsRid();
            }

            WorldActorNode? actor = FindActorFromHit(hit);
            if (actor is not null && includeActors && actor.LegacyName != excludeActor)
                hits.Add(new LegacyCastHit(actor.LegacyName, CoordinateSpace.ToLegacy(point), distance, rid));
            else if (actor is null && includeTerrain && IsTerrainCollider(hit))
                hits.Add(new LegacyCastHit(null, CoordinateSpace.ToLegacy(point), distance, rid));

            if (!excluded.Contains(rid))
                excluded.Add(rid);
            if (mode == 0 && hits.Count > 0)
                break;
        }

        if (mode == 2 || mode == 3)
        {
            if (mode == 3)
                hits.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
            return JsonSerializer.Serialize(new { hits = hits.Select(hit => new { actor = hit.Actor, point = ToVectorJson(hit.Point), distance = hit.Distance }) });
        }
        if (hits.Count == 0)
            return mode == 1 ? "null" : "false";
        if (mode == 0)
            return "true";
        LegacyCastHit closest = hits.MinBy(hit => hit.Distance);
        return JsonSerializer.Serialize(new { actor = closest.Actor, point = ToVectorJson(closest.Point), distance = closest.Distance });
    }

    private string SetTerrainCell(JsonObject args)
    {
        if (_terrain is null || args["cell"] is not JsonObject cell || args["value"] is not JsonObject value)
            return "false";
        int x = LegacySaveDocument.ReadInt(cell["x"], 0) + _terrain.Width / 2;
        int y = LegacySaveDocument.ReadInt(cell["y"], 0) - LegacyTerrainData.CellsYStart;
        int z = LegacySaveDocument.ReadInt(cell["z"], 0) + _terrain.Depth / 2;
        int shape = LegacySaveDocument.ReadInt(value["blockType"], 0) - 1;
        int direction = LegacySaveDocument.ReadInt(value["direction"], 0);
        int style = LegacySaveDocument.ReadInt(value["style"], 0);
        return _terrain.SetCell(x, y, z, shape, style, direction) ? "true" : "false";
    }

    private string GetTerrainCell(JsonObject args)
    {
        if (_terrain is null)
            return "{\"shape\":0,\"dir\":0,\"style\":0}";
        int x = LegacySaveDocument.ReadInt(args["x"], 0) + _terrain.Width / 2;
        int y = LegacySaveDocument.ReadInt(args["y"], 0) - LegacyTerrainData.CellsYStart;
        int z = LegacySaveDocument.ReadInt(args["z"], 0) + _terrain.Depth / 2;
        if (!_terrain.TryGetCell(x, y, z, out byte shape, out ushort style, out byte direction))
            return "{\"shape\":0,\"dir\":0,\"style\":0}";
        return JsonSerializer.Serialize(new { shape = shape + 1, dir = direction, style });
    }

    private string CloneActor(JsonObject args)
    {
        string baseName = LegacySaveDocument.ReadString(args["baseActorName"]);
        WorldActorNode? source = FindActor(baseName);
        if (source is null)
            return JsonSerializer.Serialize(new { error = $"Actor '{baseName}' does not exist.", names = Array.Empty<string>(), tempIds = Array.Empty<ushort>(), baseActorNames = Array.Empty<string>() });
        if (_actors.Count >= ushort.MaxValue)
            return JsonSerializer.Serialize(new { error = "The legacy behavior actor index limit was reached.", names = Array.Empty<string>(), tempIds = Array.Empty<ushort>(), baseActorNames = Array.Empty<string>() });

        Vector3 legacyPosition = LegacyActorData.ReadVector(args["position"], CoordinateSpace.ToLegacy(source.GlobalPosition));
        Quaternion legacyRotation = LegacyActorData.ReadQuaternion(args["rotation"]);
        Vector3 position = CoordinateSpace.ToGodot(legacyPosition);
        Quaternion rotation = CoordinateSpace.ToGodot(legacyRotation);
        var rootTransform = new Transform3D(new Basis(rotation).Scaled(source.GlobalTransform.Basis.Scale), position);
        var cloned = new List<(WorldActorNode Original, WorldActorNode Clone)>();
        CloneActorTree(source, source.State.TransformParent, rootTransform, cloned);

        _actorsChanged?.Invoke();
        return JsonSerializer.Serialize(new
        {
            names = cloned.Select(pair => pair.Clone.LegacyName).ToArray(),
            tempIds = cloned.Select(pair => checked((ushort)_actors.IndexOf(pair.Clone))).ToArray(),
            baseActorNames = cloned.Select(pair => pair.Original.LegacyName).ToArray()
        });
    }

    private void CloneActorTree(
        WorldActorNode source,
        string parentName,
        Transform3D targetTransform,
        List<(WorldActorNode Original, WorldActorNode Clone)> cloned)
    {
        string cloneName = Guid.NewGuid().ToString("N");
        JsonObject data = (JsonObject)source.State.Data.DeepClone();
        data["name"] = cloneName;
        data["creatorName"] = source.LegacyName;
        data["displayName"] = $"{source.State.DisplayName}-Script-Clone";
        data["cloneParent"] = source.LegacyName;
        data["wasClonedByScript"] = true;
        data["preferOffstage"] = false;
        data["transformParent"] = parentName;
        data["spawnTransformParent"] = parentName;
        data["spawnPosition"] = VectorJson(CoordinateSpace.ToLegacy(targetTransform.Origin));
        data["spawnRotation"] = QuaternionJson(CoordinateSpace.ToLegacy(targetTransform.Basis.GetRotationQuaternion()));
        var state = _document.AddActor(data);
        state.SetWorldTransform(targetTransform);
        _catalog.TryGet(state.RenderableUri, out RenderableEntry? renderable);
        var clone = new WorldActorNode();
        clone.Initialize(state, renderable);
        clone.SetPlayMode(true);
        _world.AddChild(clone);
        if (parentName.Length > 0 && FindActor(parentName) is WorldActorNode parent)
        {
            clone.Reparent(parent, keepGlobalTransform: false);
            clone.GlobalTransform = targetTransform;
        }
        else
        {
            clone.GlobalTransform = targetTransform;
        }
        _actors.Add(clone);
        _actorsByName.Add(cloneName, clone);
        _actorsById.Add(clone);
        cloned.Add((source, clone));

        foreach (WorldActorNode child in _actors.Where(actor => actor != clone && actor.State.TransformParent == source.LegacyName).ToArray())
        {
            Transform3D local = source.GlobalTransform.AffineInverse() * child.GlobalTransform;
            CloneActorTree(child, cloneName, targetTransform * local, cloned);
        }
    }

    private string InstantiatePrefab(JsonObject args)
    {
        string prefabUri = LegacySaveDocument.ReadString(args["prefabUri"]);
        const string builtinPrefix = "builtin:";
        if (!prefabUri.StartsWith(builtinPrefix, StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { name = "", brainName = "", actorId = (ushort)0 });
        string prefabName = prefabUri[builtinPrefix.Length..];
        string? path = EnumerateFiles(ActorPrefabRoot, ".actor-prefab.voos")
            .FirstOrDefault(candidate => string.Equals(
                Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(candidate)),
                prefabName,
                StringComparison.OrdinalIgnoreCase));
        if (path is null)
            return JsonSerializer.Serialize(new { name = "", brainName = "", actorId = (ushort)0 });
        if (_actors.Count >= ushort.MaxValue)
            return JsonSerializer.Serialize(new { name = "", brainName = "", actorId = (ushort)0 });

        JsonObject prefab = JsonNode.Parse(Godot.FileAccess.GetFileAsString(path)) as JsonObject
            ?? throw new InvalidDataException($"Actor prefab is invalid JSON: {path}");
        bool databaseChanged = MergePrefabBrainDatabase(prefab["brainDatabase"] as JsonObject);
        if (databaseChanged)
            _refreshDatabasePending = true;
        JsonObject data = prefab["actorData"] is JsonObject actorData
            ? (JsonObject)actorData.DeepClone()
            : throw new InvalidDataException($"Actor prefab has no actorData object: {path}");

        string name = Guid.NewGuid().ToString("N");
        data["name"] = name;
        data["creatorName"] = LegacySaveDocument.ReadString(args["creatorName"]);
        data["displayName"] = $"{LegacySaveDocument.ReadString(data["displayName"], prefabName)}-Script-Instance";
        data["preferOffstage"] = false;
        data["wasClonedByScript"] = true;
        data["transformParent"] = "";
        data["spawnTransformParent"] = "";
        Vector3 legacyPosition = LegacyActorData.ReadVector(args["position"], Vector3.Zero);
        Quaternion legacyRotation = LegacyActorData.ReadQuaternion(args["rotation"]);
        var transform = new Transform3D(
            new Basis(CoordinateSpace.ToGodot(legacyRotation)),
            CoordinateSpace.ToGodot(legacyPosition));
        data["spawnPosition"] = VectorJson(legacyPosition);
        data["spawnRotation"] = QuaternionJson(legacyRotation);
        LegacyActorData state = _document.AddActor(data);
        state.SetWorldTransform(transform);
        _catalog.TryGet(state.RenderableUri, out RenderableEntry? renderable);
        var actor = new WorldActorNode();
        actor.Initialize(state, renderable);
        actor.SetPlayMode(true);
        _world.AddChild(actor);
        actor.GlobalTransform = transform;
        _actors.Add(actor);
        _actorsByName.Add(name, actor);
        _actorsById.Add(actor);
        _actorsChanged?.Invoke();
        return JsonSerializer.Serialize(new { name, brainName = state.BrainName, actorId = checked((ushort)(_actors.Count - 1)) });
    }

    private bool MergePrefabBrainDatabase(JsonObject? source)
    {
        if (source is null)
            return false;
        JsonObject target = LegacySaveDocument.EnsureObject(_document.Root, "behaviorDatabase");
        bool changed = false;
        changed |= MergeDatabasePair(source, target, "behaviorIds", "behaviors");
        changed |= MergeDatabasePair(source, target, "behaviorUseIds", "behaviorUses");
        changed |= MergeDatabasePair(source, target, "brainIds", "brains");
        return changed;
    }

    private static bool MergeDatabasePair(JsonObject source, JsonObject target, string idsName, string valuesName)
    {
        if (source[idsName] is not JsonArray sourceIds || source[valuesName] is not JsonArray sourceValues)
            return false;
        JsonArray targetIds = target[idsName] as JsonArray ?? (JsonArray)(target[idsName] = new JsonArray())!;
        JsonArray targetValues = target[valuesName] as JsonArray ?? (JsonArray)(target[valuesName] = new JsonArray())!;
        var existing = new HashSet<string>(targetIds.Select(value => LegacySaveDocument.ReadString(value)), StringComparer.Ordinal);
        bool changed = false;
        int count = Math.Min(sourceIds.Count, sourceValues.Count);
        for (int index = 0; index < count; index++)
        {
            string id = LegacySaveDocument.ReadString(sourceIds[index]);
            if (id.Length == 0 || !existing.Add(id))
                continue;
            targetIds.Add(JsonValue.Create(id));
            targetValues.Add(sourceValues[index]?.DeepClone());
            changed = true;
        }
        return changed;
    }

    private void QueueActorDestruction(JsonArray? names)
    {
        if (names is null)
            return;
        foreach (JsonNode? node in names)
            QueueActorDestruction(LegacySaveDocument.ReadString(node));
    }

    private void QueueActorDestruction(string actorName)
    {
        WorldActorNode? actor = FindActor(actorName);
        if (actor is not null && !actor.LegacyName.StartsWith("builtin:", StringComparison.InvariantCulture))
            _actorsToDestroy.Add(actorName);
    }

    private void ApplyPendingPlayerControlTransfer()
    {
        if (_pendingPlayerControlTransfer is not { } request)
            return;
        _pendingPlayerControlTransfer = null;
        _playerControlSlots.Reconcile(_actors);

        int localPlayerSlot = GetLocalPlayerSlot();
        WorldActorNode? current = _playerControlSlots.GetActorAtSlot(_actors, localPlayerSlot);
        if (current is null || current.LegacyName != request.FromActor)
            return;
        WorldActorNode? target = request.ToActor.Length > 0 ? FindActor(request.ToActor) : null;
        if (target == current)
            return;

        bool targetNeedsPhysics = target is not null && !target.State.IsPlayerControllable;
        if (!_playerControlSlots.TryTransferControl(localPlayerSlot, request.FromActor, target))
            return;
        if (targetNeedsPhysics)
            target!.ApplyLegacyPhysicsSettings();

        if (request.Destroy)
            QueueActorDestruction(current.LegacyName);
        DestroyPendingActors();
    }

    private string TransferPlayerControl(JsonObject args)
    {
        string fromName = LegacySaveDocument.ReadString(args["fromActor"]);
        int localPlayerSlot = GetLocalPlayerSlot();
        WorldActorNode? current = _playerControlSlots.GetActorAtSlot(_actors, localPlayerSlot);
        if (current is not null && current.LegacyName == fromName)
        {
            _pendingPlayerControlTransfer = (
                fromName,
                LegacySaveDocument.ReadString(args["toActor"]),
                LegacySaveDocument.ReadBool(args["destroy"]));
        }
        return "true";
    }

    private string RequestCameraOffset(JsonObject args)
    {
        string actorName = LegacySaveDocument.ReadString(args["actor"]);
        WorldActorNode? player = GetLocalPlayerActor();
        if (player is not null && actorName == player.LegacyName)
            _requestCameraOffset?.Invoke(LegacyActorData.ReadVector(args["offset"], Vector3.Zero));
        return "true";
    }

    private int GetLocalPlayerSlot()
        => _lanSession?.IsClient == true ? _lanSession.LocalPlayerSlot : 1;

    private WorldActorNode? GetLocalPlayerActor()
        => _playerControlSlots.GetActorAtSlot(_actors, GetLocalPlayerSlot());

    internal WorldActorNode? GetPlayerActorAtSlot(int targetSlot)
        => _playerControlSlots.GetActorAtSlot(_actors, targetSlot);

    private string ProjectPoint(JsonObject args)
    {
        Vector3 worldPosition = CoordinateSpace.ToGodot(LegacyActorData.ReadVector(args, Vector3.Zero));
        if (IsBehindCamera(worldPosition))
            return "null";
        Vector2 point = ToGameUiPoint(_camera.UnprojectPosition(worldPosition));
        return JsonSerializer.Serialize(new { x = point.X, y = point.Y });
    }

    private string ProjectSphere(JsonObject args)
    {
        Vector3 center = CoordinateSpace.ToGodot(LegacyActorData.ReadVector(args["center"], Vector3.Zero));
        float radius = Math.Max(0f, LegacySaveDocument.ReadFloat(args["radius"], 0f));
        if (IsBehindCamera(center))
            return "null";
        Vector2 screenCenter = ToGameUiPoint(_camera.UnprojectPosition(center));
        Vector3 right = _camera.GlobalBasis.X;
        Vector2 screenRight = ToGameUiPoint(_camera.UnprojectPosition(center + right * radius));
        return JsonSerializer.Serialize(new { center = new { x = screenCenter.X, y = screenCenter.Y, z = 0f }, radius = Math.Abs(screenRight.X - screenCenter.X) });
    }

    private string GetActorScreenRect(JsonObject args)
    {
        WorldActorNode? actor = FindActor(LegacySaveDocument.ReadString(args["actor"]));
        if (actor is null)
            return "null";
        Aabb bounds = actor.GetWorldRenderBounds();
        Vector2 min = new(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 max = new(float.NegativeInfinity, float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = bounds.Position + new Vector3(
                (corner & 1) == 0 ? 0f : bounds.Size.X,
                (corner & 2) == 0 ? 0f : bounds.Size.Y,
                (corner & 4) == 0 ? 0f : bounds.Size.Z);
            if (IsBehindCamera(point))
                return "null";
            Vector2 projected = ToGameUiPoint(_camera.UnprojectPosition(point));
            min.X = Math.Min(min.X, projected.X);
            min.Y = Math.Min(min.Y, projected.Y);
            max.X = Math.Max(max.X, projected.X);
            max.Y = Math.Max(max.Y, projected.Y);
        }
        return JsonSerializer.Serialize(new { x = min.X, y = min.Y, w = max.X - min.X, h = max.Y - min.Y });
    }

    private bool IsBehindCamera(Vector3 worldPoint) => _camera.ToLocal(worldPoint).Z >= 0f;

    private Vector2 ToGameUiPoint(Vector2 screenPoint)
    {
        Vector2 size = _camera.GetViewport().GetVisibleRect().Size;
        float scale = size.X > 0f ? 1600f / size.X : 1f;
        return screenPoint * scale;
    }

    internal string PlaySound(JsonObject args)
    {
        string soundId = LegacySaveDocument.ReadString(args["soundId"]);
        AudioStream? stream = GetSound(soundId, out bool spatialized);
        if (stream is null)
            return "false";
        string actorName = LegacySaveDocument.ReadString(args["actorName"]);
        WorldActorNode? actor = actorName.Length > 0 ? FindActor(actorName) : null;
        if (actorName.Length > 0 && actor is null)
            return "false";
        Vector3 position = actor?.GlobalPosition ?? CoordinateSpace.ToGodot(LegacyActorData.ReadVector(args["position"], Vector3.Zero));
        if (actor is not null && !spatialized)
        {
            var player = new AudioStreamPlayer { Name = "LegacySound", Stream = stream, Bus = LegacyAudioSettings.SfxBusName };
            actor.AddChild(player);
            player.Finished += player.QueueFree;
            player.Play();
            return "true";
        }

        var spatialPlayer = new AudioStreamPlayer3D
        {
            Name = "LegacySound",
            Stream = stream,
            Bus = LegacyAudioSettings.SfxBusName
        };
        if (actor is not null)
        {
            actor.AddChild(spatialPlayer);
            spatialPlayer.Position = Vector3.Zero;
        }
        else
        {
            _world.AddChild(spatialPlayer);
            spatialPlayer.GlobalPosition = position;
        }
        spatialPlayer.Finished += spatialPlayer.QueueFree;
        spatialPlayer.Play();
        return "true";
    }

    private AudioStream? GetSound(string soundId, out bool spatialized)
    {
        spatialized = true;
        if (_soundCache.TryGetValue(soundId, out AudioStream? cached))
        {
            if (_soundSpatialization.TryGetValue(soundId, out bool cachedSpatialization))
                spatialized = cachedSpatialization;
            return cached;
        }
        JsonObject? sojo = FindSojo(soundId, "SoundEffect");
        string contentText = LegacySaveDocument.ReadString(sojo?["content"]);
        if (contentText.Length == 0 || JsonNode.Parse(contentText) is not JsonObject content)
            return null;
        spatialized = LegacySaveDocument.ReadBool(content["spatialized"], true);
        AudioStream? result = LegacySaveDocument.ReadInt(content["effectType"], 0) switch
        {
            0 when content["synthParams"] is JsonObject synth => CreateSynthAudioStream(synth),
            2 => LegacyAudioAssetCatalog.LoadStream(_document.SourcePath, content),
            _ => null
        };
        if (result is null)
            return null;
        _soundCache.Add(soundId, result);
        _soundSpatialization.Add(soundId, spatialized);
        return result;
    }

    internal static AudioStreamWav CreateSynthAudioStream(JsonObject synth)
    {
        const int sampleRate = 44100;
        const int numSlices = 32;
        int speed = Math.Clamp(LegacySaveDocument.ReadInt(synth["speed"], 8), 0, 16);
        int samplesPerSlice = Math.Clamp(300 + (16 - speed) * (3000 - 300) / 16, 300, 3000);
        int sampleCount = numSlices * samplesPerSlice;
        int[] volume = ReadIntArray(synth["volume"], numSlices, 16);
        int[] pitch = ReadIntArray(synth["pitch"], numSlices, 32);
        int wave = LegacySaveDocument.ReadInt(synth["waveShape"], 0);
        byte[] data = new byte[sampleCount * sizeof(short)];
        float currentVolume = volume[0];
        bool pitchChangeOk = true;
        int currentPitch = 0;
        int currentPitchStart = 0;
        uint noise = 0xA341316Cu;
        const float c4Log = 5.56693129191f;
        const float semitoneLog = 0.05776067245167f;

        for (int sample = 0; sample < sampleCount; sample++)
        {
            int slice = sample / samplesPerSlice;
            if (pitchChangeOk && pitch[slice] != currentPitch)
            {
                currentPitch = pitch[slice];
                currentPitchStart = sample;
                pitchChangeOk = false;
            }
            float frequency = MathF.Exp(c4Log + (currentPitch - 32) * semitoneLog);
            float samplesPerPeriod = sampleRate / frequency;
            float offset = (sample - currentPitchStart) % samplesPerPeriod / samplesPerPeriod;
            float nextOffset = (sample + 1 - currentPitchStart) % samplesPerPeriod / samplesPerPeriod;
            float waveValue = wave switch
            {
                1 => offset < 0.25f ? offset * 4f : offset < 0.75f ? 1f - (offset - 0.25f) * 4f : -1f + (offset - 0.75f) * 4f,
                2 => MathF.Sin(offset * MathF.PI),
                3 => NextNoise(ref noise) * 2f - 1f,
                _ => offset < 0.5f ? 0.4f : -0.4f
            };
            waveValue *= currentVolume / 16f;
            currentVolume = MoveTowards(currentVolume, volume[slice], 0.05f);
            pitchChangeOk = nextOffset < offset;
            short pcm = (short)Math.Clamp((int)(Math.Clamp(waveValue, -1f, 1f) * short.MaxValue), short.MinValue, short.MaxValue);
            data[sample * 2] = (byte)(pcm & 0xff);
            data[sample * 2 + 1] = (byte)((pcm >> 8) & 0xff);
        }

        return new AudioStreamWav
        {
            Data = data,
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = sampleRate,
            Stereo = false
        };
    }


    private string SpawnParticleEffect(JsonObject args)
    {
        string pfxId = LegacySaveDocument.ReadString(args["pfxId"]);
        Vector3 position = LegacyActorData.ReadVector(args["position"], Vector3.Zero);
        Vector3 rotation = LegacyActorData.ReadVector(args["rotation"], Vector3.Zero);
        float scale = LegacySaveDocument.ReadFloat(args["scale"], 1f);
        if (!SpawnParticleEffectLocal(pfxId, position, rotation, scale))
            return "false";
        _lanSession?.BroadcastParticleEffect(pfxId, position, rotation, scale);
        return "true";
    }

    public void SpawnNetworkParticleEffect(string pfxId, Vector3 position, Vector3 rotation, float scale)
    {
        if (_lanSession?.IsClient == true)
            SpawnParticleEffectLocal(pfxId, position, rotation, scale);
    }

    private bool SpawnParticleEffectLocal(string pfxId, Vector3 legacyPosition, Vector3 legacyRotation, float scale)
    {
        if (!IsValidParticleEffectRequest(pfxId, legacyPosition, legacyRotation, scale))
            return false;
        JsonObject? sojo = FindSojo(pfxId, "ParticleEffect");
        string content = LegacySaveDocument.ReadString(sojo?["content"]);
        if (content.Length == 0 || JsonNode.Parse(content) is not JsonObject effect)
            return false;

        float lifetime = Math.Max(0.05f, LegacySaveDocument.ReadFloat(effect["duration"], 1f));
        CpuParticles3D particles = LegacyParticleEffectFactory.Create(effect, stream: false);
        particles.Position = CoordinateSpace.ToGodot(legacyPosition);
        particles.Rotation = CoordinateSpace.ToGodot(legacyRotation);
        particles.Scale = Vector3.One * scale;
        _world.AddChild(particles);
        var timer = new Godot.Timer { OneShot = true, WaitTime = lifetime + 0.5f };
        particles.AddChild(timer);
        timer.Timeout += particles.QueueFree;
        timer.Start();
        return true;
    }

    private static bool IsValidParticleEffectRequest(string pfxId, Vector3 position, Vector3 rotation, float scale)
        => pfxId.Length is > 0 and <= 256
            && float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
            && float.IsFinite(rotation.X) && float.IsFinite(rotation.Y) && float.IsFinite(rotation.Z)
            && float.IsFinite(scale);

    private JsonObject? FindSojo(string id, string contentType)
    {
        if (_document.Root["sojoDatabase"]?["sojos"] is not JsonArray sojos)
            return null;
        foreach (JsonNode? node in sojos)
        {
            if (node is JsonObject sojo
                && LegacySaveDocument.ReadString(sojo["id"]) == id
                && LegacySaveDocument.ReadString(sojo["contentType"]) == contentType)
                return sojo;
        }
        return null;
    }

    private string SetSkyType(JsonObject args)
    {
        string requested = LegacySaveDocument.ReadString(args["value"]);
        string? skyType = requested.ToLowerInvariant() switch
        {
            "day" => "Day",
            "space" => "Space",
            "overcast" => "Overcast",
            "solidcolor" => "SolidColor",
            _ => null
        };
        if (skyType is null)
            return "false";
        JsonObject stage = GetStage();
        stage["skyType"] = skyType;
        ApplySkyType(skyType);
        return "true";
    }

    private string SetSkyColor(JsonObject args)
    {
        if (args["value"] is not JsonObject color)
            return "false";
        Color value = ReadColor(color);
        JsonObject stage = GetStage();
        stage["skyColor"] = ColorJson(value);
        ApplySkyType(LegacySaveDocument.ReadString(stage["skyType"], "Day"));
        return "true";
    }

    private string SetSceneLighting(JsonObject args)
    {
        string requested = LegacySaveDocument.ReadString(args["value"]);
        string? mode = requested.ToLowerInvariant() switch
        {
            "day" => "Day",
            "night" => "Night",
            "dark" => "Dark",
            _ => null
        };
        if (mode is null)
            return "false";
        GetStage()["sceneLightingMode"] = mode;
        ApplySceneLighting(mode);
        return "true";
    }

    private Color ReadStageColor(string property)
    {
        JsonObject? stage = _document.Stage;
        return stage?[property] is JsonObject color ? ReadColor(color) : new Color(0.5f, 0.5f, 0.5f, 1f);
    }

    private void ApplySkyType(string skyType)
    {
        WorldEnvironment? worldEnvironment = _world.GetParent()?.GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
        if (worldEnvironment?.Environment is not Godot.Environment environment)
            return;
        environment.BackgroundMode = Godot.Environment.BGMode.Color;
        environment.BackgroundColor = skyType.ToLowerInvariant() switch
        {
            "day" => new Color("#65B6FB"),
            "space" => new Color("#3A1A24"),
            "overcast" => new Color("#D2DCF7"),
            _ => ReadStageColor("skyColor")
        };
    }

    private void ApplySceneLighting(string mode)
    {
        float ambientEnergy = mode.ToLowerInvariant() switch { "night" => 0.28f, "dark" => 0.08f, _ => 0.65f };
        float sunEnergy = mode.ToLowerInvariant() switch { "night" => 0.45f, "dark" => 0.12f, _ => 1.2f };
        WorldEnvironment? worldEnvironment = _world.GetParent()?.GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
        if (worldEnvironment?.Environment is Godot.Environment environment)
            environment.AmbientLightEnergy = ambientEnergy;
        if (_world.GetParent()?.GetNodeOrNull<DirectionalLight3D>("Sun") is DirectionalLight3D sun)
            sun.LightEnergy = sunEnergy;
    }

    private JsonObject GetStage()
    {
        JsonObject stage = LegacySaveDocument.EnsureObject(_document.Root, "stage");
        if (stage["skyType"] is null)
            stage["skyType"] = "Day";
        if (stage["sceneLightingMode"] is null)
            stage["sceneLightingMode"] = "Day";
        return stage;
    }

    private static Color ReadColor(JsonObject color)
    {
        return new Color(
            LegacySaveDocument.ReadFloat(color["r"], 1f),
            LegacySaveDocument.ReadFloat(color["g"], 1f),
            LegacySaveDocument.ReadFloat(color["b"], 1f),
            LegacySaveDocument.ReadFloat(color["a"], 1f));
    }

    private static JsonObject ColorJson(Color color) => new()
    {
        ["r"] = color.R,
        ["g"] = color.G,
        ["b"] = color.B,
        ["a"] = color.A
    };

    private static JsonObject VectorJson(Vector3 value) => new()
    {
        ["x"] = value.X,
        ["y"] = value.Y,
        ["z"] = value.Z
    };

    private static JsonObject QuaternionJson(Quaternion value) => new()
    {
        ["x"] = value.X,
        ["y"] = value.Y,
        ["z"] = value.Z,
        ["w"] = value.W
    };

    private static int[] ReadIntArray(JsonNode? node, int count, int fallback)
    {
        var values = Enumerable.Repeat(fallback, count).ToArray();
        if (node is not JsonArray array)
            return values;
        for (int index = 0; index < Math.Min(count, array.Count); index++)
            values[index] = LegacySaveDocument.ReadInt(array[index], fallback);
        return values;
    }

    private static float MoveTowards(float current, float target, float maxDelta)
        => Math.Abs(target - current) <= maxDelta ? target : current + Math.Sign(target - current) * maxDelta;

    private static float NextNoise(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state & 0x00ffffffu) / 16777216f;
    }

    private WorldActorNode? FindActorFromHit(Godot.Collections.Dictionary hit)
    {
        if (!hit.ContainsKey("collider"))
            return null;
        GodotObject? collider = hit["collider"].AsGodotObject();
        if (collider is not Node node)
            return null;
        foreach (WorldActorNode actor in _actors)
        {
            if (actor == node || actor.IsAncestorOf(node))
                return actor;
        }
        return null;
    }

    private static bool IsTerrainCollider(Godot.Collections.Dictionary hit)
    {
        if (!hit.ContainsKey("collider") || hit["collider"].AsGodotObject() is not Node node)
            return false;
        return node.Name == "TerrainCollision" || node.Name == "StageGroundCollision";
    }

    private readonly record struct LegacyCastHit(string? Actor, Vector3 Point, float Distance, Rid Rid);
}
