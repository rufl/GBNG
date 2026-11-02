using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GBNG.Assets;
using GBNG.Domain;
using GBNG.World;
using Godot;
using Jint;

namespace GBNG.Behaviors;

public sealed class LegacyBehaviorRuntime
{
    private static readonly Regex GuidPattern = new(@"guid:\s*([0-9a-f]{32})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExportPattern = new(@"(?m)^\s*export\s+(?=(?:async\s+)?(?:function|const|let|var|class)\s+([A-Za-z_$][\w$]*))", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string BuiltinBehaviorRoot = "res://Assets/StreamingAssets/BehaviorLibrary";
    private const string ScriptingSystemsPath = "res://Assets/Prefabs/Services/ScriptingSystems.prefab";

    private readonly List<WorldActorNode> _actors;
    private readonly LegacySaveDocument _document;
    private readonly LegacyRenderableCatalog _catalog;
    private readonly Node3D _world;
    private readonly Camera3D _camera;
    private readonly Jint.Engine _engine;
    private readonly Dictionary<string, WorldActorNode> _actorsByName = new(StringComparer.Ordinal);
    private readonly List<WorldActorNode> _actorsById = new();
    private double _gameTime;

    public LegacyBehaviorRuntime(
        List<WorldActorNode> actors,
        LegacySaveDocument document,
        LegacyRenderableCatalog catalog,
        Node3D world,
        Camera3D camera)
    {
        _actors = actors;
        _document = document;
        _catalog = catalog;
        _world = world;
        _camera = camera;
        _engine = new Jint.Engine(options => options
            .MaxStatements(1_000_000)
            .TimeoutInterval(TimeSpan.FromMilliseconds(250)));

        RegisterHostBindings();
        _engine.Execute(ReadEngineSource(), "GBNG behavior engine");
        RegisterBuiltInModules();
        RegisterEmbeddedModules();
        ConfigureBehaviorDatabase();
        SyncActors();
    }

    public void Tick(double deltaSeconds)
    {
        if (deltaSeconds <= 0d)
            return;

        _gameTime += deltaSeconds;
        SyncActors();
        _engine.SetValue("__gbDelta", deltaSeconds);
        _engine.SetValue("__gbGameTime", _gameTime);
        _engine.Execute("__gbTick(__gbDelta, __gbGameTime);");
        ApplyRuntimeResponse();
        PersistRuntimeMemory();
    }

    private void RegisterHostBindings()
    {
        _engine.SetValue("getActorString", new Func<int, int, string>(GetActorString));
        _engine.SetValue("setActorString", new Action<int, int, string>(SetActorString));
        _engine.SetValue("getActorBoolean", new Func<int, int, bool>(GetActorBoolean));
        _engine.SetValue("setActorBoolean", new Action<int, int, bool>(SetActorBoolean));
        _engine.SetValue("getActorFloat", new Func<int, int, double>(GetActorFloat));
        _engine.SetValue("setActorFloat", new Action<int, int, double>(SetActorFloat));
        _engine.SetValue("setActorVector3", new Action<int, int, double, double, double>(SetActorVector3));
        _engine.SetValue("setActorQuaternion", new Action<int, int, double, double, double, double>(SetActorQuaternion));
        _engine.SetValue("__gbReadActorVector3", new Func<int, int, string>(ReadActorVector3Json));
        _engine.SetValue("__gbReadActorQuaternion", new Func<int, int, string>(ReadActorQuaternionJson));
        _engine.SetValue("__gbService", new Func<string, string, string>(CallService));

        _engine.Execute(BridgeSource, "GBNG behavior bridge");
    }

    private const string BridgeSource = @"
const __gbBehaviorModules = new Map();
function getVoosModule(key) { return __gbBehaviorModules.get(key); }
function __gbRegisterModule(key, body) {
  const module = new Function(body)();
  __gbBehaviorModules.set(key, module);
}
function callVoosService(name, args = {}) {
  return JSON.parse(__gbService(name, JSON.stringify(args)));
}
function getActorVector3(actorId, fieldId, result) {
  const value = JSON.parse(__gbReadActorVector3(actorId, fieldId));
  result.set(value.x, value.y, value.z);
}
function getActorQuaternion(actorId, fieldId, result) {
  const value = JSON.parse(__gbReadActorQuaternion(actorId, fieldId));
  result.set(value.x, value.y, value.z, value.w);
}
function __gbConfigureDatabase(database) {
  moduleBehaviors_.database.reset(database);
  initBehaviorsPending = true;
}
function __gbSyncActors(states) {
  const liveNames = new Set();
  const orderedNames = [];
  for (let i = 0; i < states.length; i++) {
    const state = states[i];
    liveNames.add(state.name);
    orderedNames.push(state.name);
    let actor = moduleBehaviors_.actors_.get(state.name);
    if (!actor) {
      actor = new Actor(state.name, moduleBehaviors_);
      actor.memory = unpackObj(JSON.parse(state.memoryJson || '{}'));
      actor.isLocalActor = true;
      moduleBehaviors_.actors_.set(state.name, actor);
    }
    actor.tempId_ = i;
    actor.brainName = state.brainName;
    actor.isOffstage = state.preferOffstage;
    actor.isOffstageEffective = state.preferOffstage;
    actor.spawnPosition = new THREE.Vector3(state.spawnPosition.x, state.spawnPosition.y, state.spawnPosition.z);
    actor.spawnRotation = new THREE.Quaternion(state.spawnRotation.x, state.spawnRotation.y, state.spawnRotation.z, state.spawnRotation.w);
    actor.transformParent = state.transformParent;
    actor.mergeJsonObject({
      name: state.name,
      isSprinting: state.isSprinting,
      worldSpaceThrottle: new THREE.Vector3(state.worldSpaceThrottle.x, state.worldSpaceThrottle.y, state.worldSpaceThrottle.z),
      inputThrottle: new THREE.Vector3(state.inputThrottle.x, state.inputThrottle.y, state.inputThrottle.z),
      aimDirection: new THREE.Vector3(0, 0, 1),
      aimOrigin: new THREE.Vector3(state.position.x, state.position.y, state.position.z),
      lastAimHitPoint: new THREE.Vector3(),
      aimingAtName: '',
      lookAxes: new THREE.Vector3()
    });
  }
  for (const name of moduleBehaviors_.actors_.keys()) {
    if (!liveNames.has(name)) moduleBehaviors_.actors_.delete(name);
  }
  moduleBehaviors_.namesById_ = orderedNames;
}
function __gbTick(dt, gameTime) {
  response = {
    playerToolTips: [], velocityChanges: [], torqueRequests: [],
    messagesToRemoteActors: [], messagesToUnity: []
  };
  ApiV2Context.setup(dt);
  try {
    moduleBehaviors_.prepareForTick();
    if (initBehaviorsPending) {
      initBehaviorsPending = false;
      moduleBehaviors_.initNewBehaviorUses(gameTime);
    }
    moduleBehaviors_.sendMessageToAll('Tick', {dt: dt, time: gameTime}, null, {onstage: true, offstage: false});
    moduleBehaviors_.sendMessageToAll('OffstageTick', {dt: dt, time: gameTime}, null, {onstage: false, offstage: true});
    moduleBehaviors_.sendMessageToAll('LocalTick', {dt: dt, time: gameTime}, null, {onstage: true, offstage: false});
    moduleBehaviors_.update(dt, gameTime);
    ApiV2Context.instance.endFrame();
    globalThis.__gbResponseJson = JSON.stringify(response);
    globalThis.__gbMemoryJson = JSON.stringify(Array.from(moduleBehaviors_.actors_.values(), actor => ({
      name: actor.name, memoryJson: actor.getMemoryJson(), requestedDestroy: actor.requestedDestroy
    })));
  } finally {
    ApiV2Context.tearDown();
  }
}
";

    private string ReadEngineSource()
    {
        string manifest = Godot.FileAccess.GetFileAsString(ScriptingSystemsPath);
        Match sourcesMatch = Regex.Match(manifest, @"(?ms)^\s*sources:\s*(?<sources>.*?)(?=^\s*typings:)");
        if (!sourcesMatch.Success)
            throw new InvalidDataException($"The behavior source list is missing from {ScriptingSystemsPath}.");

        var pathByGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string metaPath in EnumerateFiles("res://Assets", ".meta"))
        {
            string metadata = Godot.FileAccess.GetFileAsString(metaPath);
            Match guidMatch = GuidPattern.Match(metadata);
            if (!guidMatch.Success)
                continue;
            string sourcePath = metaPath[..^5];
            if (Godot.FileAccess.FileExists(sourcePath))
                pathByGuid[guidMatch.Groups[1].Value] = sourcePath;
        }

        var source = new StringBuilder();
        foreach (Match guidMatch in GuidPattern.Matches(sourcesMatch.Groups["sources"].Value))
        {
            string guid = guidMatch.Groups[1].Value;
            if (!pathByGuid.TryGetValue(guid, out string? sourcePath))
                throw new FileNotFoundException($"Behavior source GUID {guid} in {ScriptingSystemsPath} has no text asset.");
            source.AppendLine(Godot.FileAccess.GetFileAsString(sourcePath));
        }

        source.AppendLine(ActorHostSource);
        source.AppendLine(BehaviorSystemHostSource);
        return source.ToString();
    }

    private const string ActorHostSource = @"
function __gbActorStatesJson(statesJson) { __gbSyncActors(JSON.parse(statesJson)); }
";

    private const string BehaviorSystemHostSource = @"
function __gbInstallDatabase(databaseJson) { __gbConfigureDatabase(JSON.parse(databaseJson)); }
";

    private void RegisterBuiltInModules()
    {
        foreach (string path in EnumerateFiles(BuiltinBehaviorRoot, ".js.txt"))
        {
            string moduleName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
            if (string.IsNullOrWhiteSpace(moduleName))
                continue;
            RegisterModule($"builtin:{moduleName}", Godot.FileAccess.GetFileAsString(path));
        }
    }

    private void RegisterEmbeddedModules()
    {
        JsonObject? database = _document.BehaviorDatabase;
        if (database?['"'"'behaviorIds'"'"'] is not JsonArray ids || database['"'"'behaviors'"'"'] is not JsonArray behaviors)
            return;

        int count = Math.Min(ids.Count, behaviors.Count);
        for (int i = 0; i < count; i++)
        {
            string id = LegacySaveDocument.ReadString(ids[i]);
            if (id.Length == 0 || behaviors[i] is not JsonObject behavior)
                continue;
            string source = LegacySaveDocument.ReadString(behavior['"'"'javascript'"'"']);
            if (source.Length > 0)
                RegisterModule($"embedded:{id}", source);
        }
    }

    private void RegisterModule(string uri, string source)
    {
        MatchCollection exports = ExportPattern.Matches(source);
        var names = new List<string>(exports.Count + 1) { "PROPS" };
        foreach (Match export in exports)
        {
            string name = export.Groups[1].Value;
            if (!names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }

        string body = ExportPattern.Replace(source, "");
        string exportObject = string.Join(",", names.Select(name => $"{name}: (typeof {name} === 'undefined' ? undefined : {name})"));
        body += $"\nreturn {{{exportObject}}};\n";
        _engine.SetValue("__gbModuleKey", uri);
        _engine.SetValue("__gbModuleBody", body);
        _engine.Execute("__gbRegisterModule(__gbModuleKey, __gbModuleBody);");
    }

    private void ConfigureBehaviorDatabase()
    {
        JsonObject? database = _document.BehaviorDatabase;
        if (database is null)
            return;

        _engine.SetValue("__gbDatabaseJson", database.ToJsonString());
        _engine.Execute("__gbInstallDatabase(__gbDatabaseJson);");
    }

    private void SyncActors()
    {
        _actorsByName.Clear();
        _actorsById.Clear();
        var states = new List<object>(_actors.Count);
        foreach (WorldActorNode actor in _actors)
        {
            _actorsByName.Add(actor.LegacyName, actor);
            _actorsById.Add(actor);
            Vector3 playerInput = actor.State.IsPlayerControllable ? ReadPlayerThrottle() : Vector3.Zero;
            Vector3 forward = CoordinateSpace.ToLegacy(-_camera.GlobalBasis.Z);
            forward.Y = 0f;
            forward = forward.LengthSquared() > 0f ? forward.Normalized() : Vector3.Forward;
            Vector3 right = CoordinateSpace.ToLegacy(_camera.GlobalBasis.X);
            right.Y = 0f;
            right = right.LengthSquared() > 0f ? right.Normalized() : Vector3.Right;
            Vector3 worldThrottle = (right * playerInput.X + forward * playerInput.Y).LimitLength(1f);
            Vector3 position = CoordinateSpace.ToLegacy(actor.GlobalPosition);
            Vector3 spawnPosition = actor.State.ReadLegacyVector("spawnPosition", actor.State.ReadLegacyVector("position", Vector3.Zero));
            Quaternion spawnRotation = actor.State.ReadLegacyQuaternion("spawnRotation");
            states.Add(new
            {
                name = actor.LegacyName,
                brainName = actor.State.BrainName,
                memoryJson = actor.State.MemoryJson,
                preferOffstage = actor.State.IsOffstage,
                transformParent = actor.State.TransformParent,
                spawnPosition = ToVectorJson(spawnPosition),
                spawnRotation = ToQuaternionJson(spawnRotation),
                position = ToVectorJson(position),
                isSprinting = Input.IsKeyPressed(Key.Shift),
                worldSpaceThrottle = ToVectorJson(worldThrottle),
                inputThrottle = ToVectorJson(playerInput)
            });
        }

        _engine.SetValue("__gbActorStates", JsonSerializer.Serialize(states));
        _engine.Execute("__gbActorStatesJson(__gbActorStates);");
    }

    private Vector3 ReadPlayerThrottle()
    {
        Vector2 input = Vector2.Zero;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) input.X -= 1f;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) input.X += 1f;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) input.Y += 1f;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) input.Y -= 1f;
        return new Vector3(input.X, input.Y, 0f).LimitLength(1f);
    }

    private void ApplyRuntimeResponse()
    {
        string responseJson = _engine.GetValue("__gbResponseJson").AsString();
        if (JsonNode.Parse(responseJson) is JsonObject response)
        {
            if (response["velocityChanges"] is JsonArray velocityChanges)
            {
                foreach (JsonNode? item in velocityChanges)
                {
                    if (item is not JsonObject change)
                        continue;
                    WorldActorNode? actor = FindActor(LegacySaveDocument.ReadString(change["entityName"]));
                    if (actor is null)
                        continue;
                    Vector3 delta = CoordinateSpace.ToGodot(LegacyActorData.ReadVector(change["delta"], Vector3.Zero));
                    actor.LinearVelocity += delta;
                }
            }
            if (response["torqueRequests"] is JsonArray torques)
            {
                foreach (JsonNode? item in torques)
                {
                    if (item is not JsonObject request)
                        continue;
                    WorldActorNode? actor = FindActor(LegacySaveDocument.ReadString(request["actorId"]));
                    if (actor is null || actor.Freeze)
                        continue;
                    actor.ApplyTorque(CoordinateSpace.ToGodot(LegacyActorData.ReadVector(request["torque"], Vector3.Zero)));
                }
            }
        }

        string memoryJson = _engine.GetValue("__gbMemoryJson").AsString();
        if (JsonNode.Parse(memoryJson) is not JsonArray memories)
            return;
        foreach (JsonNode? item in memories)
        {
            if (item is not JsonObject actorMemory)
                continue;
            WorldActorNode? actor = FindActor(LegacySaveDocument.ReadString(actorMemory["name"]));
            if (actor is null)
                continue;
            actor.State.Data["memoryJson"] = LegacySaveDocument.ReadString(actorMemory["memoryJson"], "{}");
            actor.ApplyDesiredVelocity();
            actor.ApplyLegacyPhysicsSettings();
        }
    }

    private void PersistRuntimeMemory()
    {
        // Runtime memory is copied back to the save document after every tick in ApplyRuntimeResponse.
    }

    private string GetActorString(int actorId, int fieldId)
    {
        WorldActorNode? actor = GetActor(actorId);
        if (actor is null)
            return "";
        JsonObject data = actor.State.Data;
        return fieldId switch
        {
            0 => actor.State.DisplayName,
            1 => actor.State.Description,
            2 => actor.State.TransformParent,
            3 => LegacySaveDocument.ReadString(data["commentText"]),
            4 => LegacySaveDocument.ReadString(data["debugString"]),
            5 => LegacySaveDocument.ReadString(data["cloneParent"]),
            6 => data["tags"] is JsonArray tags ? string.Join(',', tags.Select(tag => LegacySaveDocument.ReadString(tag))) : "",
            7 => LegacySaveDocument.ReadString(data["cameraActor"]),
            8 => LegacySaveDocument.ReadString(data["spawnTransformParent"]),
            9 => actor.State.LoopingAnimation,
            10 => LegacySaveDocument.ReadString(data["controllingVirtualPlayerId"]),
            11 => LegacySaveDocument.ReadString(data["cameraSettingsJson"]),
            12 => LegacySaveDocument.ReadString(data["lightSettingsJson"]),
            13 => LegacySaveDocument.ReadString(data["pfxId"]),
            14 => LegacySaveDocument.ReadString(data["sfxId"]),
            _ => ""
        };
    }

    private void SetActorString(int actorId, int fieldId, string value)
    {
        WorldActorNode? actor = GetActor(actorId);
        if (actor is null)
            return;
        switch (fieldId)
        {
            case 0: actor.State.WriteString("displayName", value); break;
            case 1: actor.State.WriteString("description", value); break;
            case 2: actor.State.SetTransformParent(value); break;
            case 3: actor.State.WriteString("commentText", value); break;
            case 5:
                actor.State.Data["tags"] = new JsonArray(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray());
                break;
            case 6: actor.State.WriteString("cameraActor", value); break;
            case 7: actor.State.WriteString("spawnTransformParent", value); break;
            case 8: actor.SetLoopingAnimation(value); break;
            case 9: actor.State.WriteString("controllingVirtualPlayerId", value); break;
            case 10: actor.State.WriteString("cameraSettingsJson", value); break;
            case 11: actor.State.WriteString("lightSettingsJson", value); break;
            case 12: actor.State.WriteString("pfxId", value); break;
            case 13: actor.State.WriteString("sfxId", value); break;
        }
    }

    private bool GetActorBoolean(int actorId, int fieldId)
    {
        WorldActorNode? actor = GetActor(actorId);
        if (actor is null)
            return false;
        JsonObject data = actor.State.Data;
        return fieldId switch
        {
            0 => actor.State.IsOffstage,
            1 => actor.State.IsSolid,
            2 => actor.State.EnablePhysics,
            3 => actor.State.EnableGravity,
            4 => actor.State.FreezeRotations,
            5 => actor.State.FreezeX,
            6 => actor.State.FreezeY,
            7 => actor.State.FreezeZ,
            8 => LegacySaveDocument.ReadBool(data["enableAiming"]),
            9 => actor.State.HideInPlayMode,
            10 => LegacySaveDocument.ReadBool(data["keepUpright"]),
            11 => actor.UseDesiredVelocity,
            12 => actor.IgnoreVerticalDesiredVelocity,
            13 => actor.State.IsPlayerControllable,
            14 => LegacySaveDocument.ReadBool(data["wasClonedByScript"]),
            15 => actor.State.UseConcaveCollider,
            16 => actor.IsGrounded,
            17 => LegacySaveDocument.ReadBool(data["speculativeColDet"]),
            18 => actor.UseStickyDesiredVelocity,
            _ => false
        };
    }

    private void SetActorBoolean(int actorId, int fieldId, bool value)
    {
        WorldActorNode? actor = GetActor(actorId);
        if (actor is null)
            return;
        string? property = fieldId switch
        {
            0 => "preferOffstage", 1 => "isSolid", 2 => "enablePhysics", 3 => "enableGravity",
            4 => "freezeRotations", 5 => "freezeX", 6 => "freezeY", 7 => "freezeZ",
            8 => "enableAiming", 9 => "hideInPlayMode", 10 => "keepUpright",
            13 => "isPlayerControllable", 14 => "wasClonedByScript", 15 => "useConcaveCollider",
            16 => null, 17 => "speculativeColDet", _ => null
        };
        if (property is not null)
        {
            actor.State.WriteBool(property, value);
            actor.ApplyLegacyPhysicsSettings();
            return;
        }

        switch (fieldId)
        {
            case 11: actor.UseDesiredVelocity = value; break;
            case 12: actor.IgnoreVerticalDesiredVelocity = value; break;
            case 18: actor.UseStickyDesiredVelocity = value; break;
        }
    }

    private double GetActorFloat(int actorId, int fieldId)
    {
        WorldActorNode? actor = GetActor(actorId);
        if (actor is null)
            return 0d;
        return fieldId switch
        {
            0 => actor.State.Bounciness,
            1 => actor.State.Drag,
            2 => actor.State.AngularDrag,
            3 => actor.State.Mass,
            _ => 0d
        };
    }

    private void SetActorFloat(int actorId, int fieldId, double value)
    {
        WorldActorNode? actor = GetActor(actorId);
        if (actor is null)
            return;
        string? property = fieldId switch { 0 => "bounciness", 1 => "drag", 2 => "angularDrag", 3 => "mass", _ => null };
        if (property is null)
            return;
        actor.State.WriteFloat(property, (float)value);
        actor.ApplyLegacyPhysicsSettings();
    }

    private void SetActorVector3(int actorId, int fieldId, double x, double y, double z)
    {
        WorldActorNode? actor = GetActor(actorId);
        if (actor is null)
            return;
        Vector3 legacyValue = new((float)x, (float)y, (float)z);
        Vector3 godotValue = CoordinateSpace.ToGodot(legacyValue);
        switch (fieldId)
        {
            case 0:
                actor.GlobalPosition = godotValue;
                actor.State.SetPosition(godotValue);
                break;
            case 1: actor.Position = godotValue; break;
            case 2:
                actor.Scale = legacyValue;
                actor.State.SetScale(legacyValue);
                break;
            case 3: actor.SetRenderableOffset(legacyValue); break;
            case 4: actor.State.WriteLegacyVector("spawnPosition", legacyValue); break;
            case 5: actor.DesiredVelocity = godotValue; break;
            case 6:
                actor.LinearVelocity = godotValue;
                actor.State.SetVelocity(godotValue);
                break;
            case 7:
                actor.AngularVelocity = godotValue;
                actor.State.SetAngularVelocity(godotValue);
                break;
            case 8: actor.StickyDesiredVelocity = godotValue; break;
            case 9: actor.StickyForce = godotValue; break;
        }
    }

    private void SetActorQuaternion(int actorId, int fieldId, double x, double y, double z, double w)
    {
        WorldActorNode? actor = GetActor(actorId);
        if (actor is null)
            return;
        var legacy = new Quaternion((float)x, (float)y, (float)z, (float)w);
        Quaternion godot = CoordinateSpace.ToGodot(legacy);
        switch (fieldId)
        {
            case 0:
                actor.GlobalRotation = godot.GetEuler();
                actor.State.SetRotation(godot);
                break;
            case 1: actor.Quaternion = godot; break;
            case 2: actor.SetRenderableRotation(legacy); break;
            case 3: actor.State.WriteLegacyQuaternion("spawnRotation", legacy); break;
        }
    }

    private string ReadActorVector3Json(int actorId, int fieldId)
    {
        WorldActorNode? actor = GetActor(actorId);
        if (actor is null)
            return "{\"x\":0,\"y\":0,\"z\":0}";
        Vector3 godot = fieldId switch
        {
            0 => actor.GlobalPosition,
            1 => actor.Position,
            2 => actor.Scale,
            3 => CoordinateSpace.ToGodot(actor.State.ReadLegacyVector("renderableOffset", Vector3.Zero)),
            4 => actor.State.SpawnPosition,
            5 => actor.DesiredVelocity,
            6 => actor.GetWorldRenderBounds().Size,
            7 => CoordinateSpace.ToGodot(actor.GetWorldRenderBounds().GetCenter()),
            8 => actor.LinearVelocity,
            9 => actor.AngularVelocity,
            10 => actor.StickyDesiredVelocity,
            11 => actor.StickyForce,
            _ => Vector3.Zero
        };
        Vector3 legacy = fieldId == 2 || fieldId == 6 ? godot : CoordinateSpace.ToLegacy(godot);
        return JsonSerializer.Serialize(ToVectorJson(legacy));
    }

    private string ReadActorQuaternionJson(int actorId, int fieldId)
    {
        WorldActorNode? actor = GetActor(actorId);
        if (actor is null)
            return "{\"x\":0,\"y\":0,\"z\":0,\"w\":1}";
        Quaternion godot = fieldId switch
        {
            0 => actor.GlobalBasis.GetRotationQuaternion(),
            1 => actor.Quaternion,
            2 => CoordinateSpace.ToGodot(actor.State.ReadLegacyQuaternion("renderableRotation")),
            3 => actor.State.SpawnRotation,
            _ => Quaternion.Identity
        };
        return JsonSerializer.Serialize(ToQuaternionJson(CoordinateSpace.ToLegacy(godot)));
    }

    private string CallService(string serviceName, string argsJson)
    {
        JsonObject args = JsonNode.Parse(argsJson) as JsonObject ?? new JsonObject();
        switch (serviceName)
        {
            case "GetActorColorField":
            {
                WorldActorNode? actor = GetActor(LegacySaveDocument.ReadInt(args["actorId"], -1));
                return actor is null ? "null" : JsonSerializer.Serialize(ToColorJson(actor.State.Tint));
            }
            case "SetActorColorField":
            {
                WorldActorNode? actor = GetActor(LegacySaveDocument.ReadInt(args["actorId"], -1));
                if (actor is null || args["newValue"] is not JsonObject color)
                    return "null";
                actor.SetTint(new Color(
                    LegacySaveDocument.ReadFloat(color["r"], 1f),
                    LegacySaveDocument.ReadFloat(color["g"], 1f),
                    LegacySaveDocument.ReadFloat(color["b"], 1f),
                    LegacySaveDocument.ReadFloat(color["a"], 1f)));
                return "null";
            }
            case "GetPlayerActors":
                return JsonSerializer.Serialize(_actors.Where(actor => actor.State.IsPlayerControllable).Select(actor => actor.LegacyName));
            case "GetPlayerControlledActor":
                return JsonSerializer.Serialize(_actors.FirstOrDefault(actor => actor.State.IsPlayerControllable)?.LegacyName);
            case "GetCameraActor":
                return JsonSerializer.Serialize(_actors.Select(actor => LegacySaveDocument.ReadString(actor.State.Data["cameraActor"]))
                    .FirstOrDefault(name => name.Length > 0));
            case "GetPlayersInfo":
                return JsonSerializer.Serialize(new { allPlayers = new[] { new { id = "local", slotNumber = 1, nickname = "Local Player" } } });
            case "IsMultiplayer":
                return "false";
            case "IsMasterClient":
                return "true";
            case "GetScreenInfo":
                Vector2I screenSize = _camera.GetViewport().GetVisibleRect().Size;
                return JsonSerializer.Serialize(new { width = screenSize.X, height = screenSize.Y });
            case "GetCameraInfo":
                return JsonSerializer.Serialize(new
                {
                    position = ToVectorJson(CoordinateSpace.ToLegacy(_camera.GlobalPosition)),
                    rotation = ToQuaternionJson(CoordinateSpace.ToLegacy(_camera.GlobalBasis.GetRotationQuaternion())),
                    fov = _camera.Fov
                });
            case "OverlapSphere":
            {
                Vector3 center = CoordinateSpace.ToGodot(LegacyActorData.ReadVector(args["center"], Vector3.Zero));
                float radius = LegacySaveDocument.ReadFloat(args["radius"], 0f);
                string tag = LegacySaveDocument.ReadString(args["tag"]);
                return JsonSerializer.Serialize(_actors.Where(actor =>
                    (tag.Length == 0 || ActorHasTag(actor, tag)) && actor.GlobalPosition.DistanceTo(center) <= radius)
                    .Select(actor => actor.LegacyName));
            }
            case "RequestUi":
                return "null";
            case "ReportBehaviorException":
                GD.PushError($"Behavior runtime exception: {argsJson}");
                return "null";
            case "LogBehaviorMessage":
                GD.Print(argsJson);
                return "null";
            case "BeginProfileSample":
            case "EndProfileSample":
                return "null";
            case "GetActorScreenRect":
            case "ProjectPoint":
            case "ProjectSphere":
            case "RequestTempCameraOffset":
            case "TransferPlayerControl":
                throw new NotSupportedException($"Godot behavior service '{serviceName}' requires a scene/UI integration not yet available in this runtime.");
            case "CloneActor":
            case "InstantiatePrefab":
            case "DestroyActors":
            case "CheckBox":
            case "Cast":
            case "SetTerrainCell":
            case "GetTerrainCell":
            case "PlaySound":
            case "SpawnParticleEffect":
            case "PlayOneShotAnimation":
            case "SetSkyType":
            case "GetSkyType":
            case "SetSkyColor":
            case "GetSkyColor":
            case "SetSceneLighting":
            case "GetSceneLighting":
                throw new NotSupportedException($"Godot behavior service '{serviceName}' has no Godot implementation.");
            default:
                throw new NotSupportedException($"Unknown legacy behavior service '{serviceName}'.");
        }
    }

    private bool ActorHasTag(WorldActorNode actor, string tag)
    {
        return actor.State.Data["tags"] is JsonArray tags && tags.Any(value => LegacySaveDocument.ReadString(value) == tag);
    }

    private void ApplyRuntimeResponsePlaceholder()
    {
    }

    private void PersistRuntimeMemoryPlaceholder()
    {
    }

    private WorldActorNode? GetActor(int actorId)
        => (uint)actorId < (uint)_actorsById.Count ? _actorsById[actorId] : null;

    private WorldActorNode? FindActor(string name)
        => _actorsByName.TryGetValue(name, out WorldActorNode? actor) ? actor : null;

    private static string[] EnumerateFiles(string root, string suffix)
    {
        if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(root)))
            return Array.Empty<string>();
        var files = new List<string>();
        EnumerateFilesRecursive(root, suffix, files);
        return files.ToArray();
    }

    private static void EnumerateFilesRecursive(string directory, string suffix, List<string> files)
    {
        foreach (string file in DirAccess.GetFilesAt(directory))
        {
            if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                files.Add($"{directory}/{file}");
        }
        foreach (string child in DirAccess.GetDirectoriesAt(directory))
            EnumerateFilesRecursive($"{directory}/{child}", suffix, files);
    }

    private static object ToVectorJson(Vector3 value) => new { x = value.X, y = value.Y, z = value.Z };
    private static object ToQuaternionJson(Quaternion value) => new { x = value.X, y = value.Y, z = value.Z, w = value.W };
    private static object ToColorJson(Color value) => new { r = value.R, g = value.G, b = value.B, a = value.A };
}
