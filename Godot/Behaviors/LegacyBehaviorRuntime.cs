using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GBNG.Assets;
using GBNG.Domain;
using GBNG.World;
using GBNG.Networking;
using Godot;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace GBNG.Behaviors;

public sealed partial class LegacyBehaviorRuntime : IDisposable
{
    private static readonly Regex GuidPattern = new(@"guid:\s*([0-9a-f]{32})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExportPattern = new(@"(?m)^\s*export\s+(?=(?:async\s+)?(?:function|const|let|var|class)\s+([A-Za-z_$][\w$]*))", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JavaScriptLineColumn = new(@"(?<line>\d+):(?<column>\d+)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BehaviorStackLineColumn = new(@"\bline (?<line>\d+):(?<column>\d+)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const int FunctionConstructorLineOffset = 2;
    private const string BuiltinBehaviorRoot = "res://Assets/StreamingAssets/BehaviorLibrary";
    private const string ScriptingSystemsPath = "res://Assets/Prefabs/Services/ScriptingSystems.prefab";

    private readonly List<WorldActorNode> _actors;
    private readonly LegacySaveDocument _document;
    private readonly LegacyRenderableCatalog _catalog;
    private readonly Node3D _world;
    private readonly LegacyTerrainNode? _terrain;
    private readonly Camera3D _camera;
    private readonly Action<JsonArray> _requestUi;
    private readonly Action? _actorsChanged;
    private readonly Action<Vector3>? _requestCameraOffset;
    private readonly LegacyLanSession? _lanSession;
    private readonly Action<int, string>? _reportBehaviorException;
    private readonly V8ScriptEngine _engine;
    private long _executionDeadlineTimestamp;
    private readonly Dictionary<string, WorldActorNode> _actorsByName = new(StringComparer.Ordinal);
    private readonly List<WorldActorNode> _actorsById = new();
    private readonly HashSet<string> _actorsToDestroy = new(StringComparer.Ordinal);
    private (string FromActor, string ToActor, bool Destroy)? _pendingPlayerControlTransfer;
    private readonly HashSet<string> _collisionHandlingBrainIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _keysHeldLastTick = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteInputHistory> _remoteInputHistories = new(StringComparer.Ordinal);
    private readonly List<string> _remoteInputHistoryRemoveBuffer = new();
    private readonly HashSet<string> _actorsKnownToRuntime = new(StringComparer.Ordinal);
    private readonly List<object> _actorStatesBuffer = new();
    private readonly List<LegacyBehaviorMessage> _pendingBehaviorMessagesBuffer = new();
    private readonly List<string> _keysHeldBuffer = new();
    private readonly List<string> _keysJustPressedBuffer = new();
    private readonly List<string> _keysJustReleasedBuffer = new();
    private double _gameTime;
    private JsonArray _lastPlayerToolTips = new();
    private Vector2 _lookAxes;
    private Vector2 _mousePosition;
    private float _mouseWheelRaw;
    private bool _mouseOverUi;
    private bool _reportedMouseDown;
    private bool _collectInput;
    private bool _physicsQueryAvailable;

    private static readonly string[] ScriptableKeyNames = LegacyInputKeys.ScriptableNames;

    private const float LegacyGameUiWidth = 1600f;
    public LegacyBehaviorRuntime(
        List<WorldActorNode> actors,
        LegacySaveDocument document,
        LegacyRenderableCatalog catalog,
        Node3D world,
        LegacyTerrainNode? terrain,
        Camera3D camera,
        Action<JsonArray> requestUi,
        Action? actorsChanged = null,
        Action<Vector3>? requestCameraOffset = null,
        LegacyLanSession? lanSession = null,
        Action<int, string>? reportBehaviorException = null)
    {
        _actors = actors;
        _document = document;
        _catalog = catalog;
        _world = world;
        _terrain = terrain;
        _camera = camera;
        _requestUi = requestUi;
        _actorsChanged = actorsChanged;
        _requestCameraOffset = requestCameraOffset;
        _engine = new V8ScriptEngine();
        _lanSession = lanSession;
        _reportBehaviorException = reportBehaviorException;
        try
        {
            _engine.ContinuationCallback = ShouldContinueScript;
            RegisterHostBindings();
            ExecuteScript(ReadEngineSource(), "GBNG behavior engine", TimeSpan.FromSeconds(5));
            RegisterBuiltInModules();
            RegisterEmbeddedModules();
            ConfigureBehaviorDatabase();
            SyncActors();
            ApplySkyType(LegacySaveDocument.ReadString(_document.Stage?["skyType"], "Day"));
            ApplySceneLighting(LegacySaveDocument.ReadString(_document.Stage?["sceneLightingMode"], "Day"));
        }
        catch
        {
            _engine.Dispose();
            throw;
        }

    }
    public event Action<JsonArray>? PlayerToolTipsUpdated;
    public void Tick(double deltaSeconds)
    {
        if (deltaSeconds <= 0d)
            return;
        _gameTime += deltaSeconds;
        if (_refreshDatabasePending)
        {
            _refreshDatabasePending = false;
            RegisterBuiltInModules();
            RegisterEmbeddedModules();
            ConfigureBehaviorDatabase();
        }
        _collectInput = true;
        _physicsQueryAvailable = true;
        try
        {
            SyncActors();
        }
        finally
        {
            _collectInput = false;
            _physicsQueryAvailable = false;
        }
        SetScriptValue("__gbDelta", deltaSeconds);
        SetScriptValue("__gbGameTime", _gameTime);
        ExecuteScript("__gbTick(__gbDelta, __gbGameTime);", timeBudget: TimeSpan.FromSeconds(1));
        ApplyRuntimeResponse(deltaSeconds);
        ApplyPendingPlayerControlTransfer();
    }

    internal void SetLookAxes(Vector2 lookAxes) => _lookAxes = lookAxes;

    internal void SetPointerState(Vector2 viewportPosition, float mouseWheelRaw, bool mouseOverUi)
    {
        _mousePosition = viewportPosition;
        _mouseWheelRaw = mouseWheelRaw;
        _mouseOverUi = mouseOverUi;
    }

    public void SendUiMessage(string actorName, string messageName, string argumentJson)
    {
        SetScriptValue("__gbUiActor", actorName);
        SetScriptValue("__gbUiMessageName", messageName);
        SetScriptValue("__gbUiArgumentJson", argumentJson.Length == 0 ? "{}" : argumentJson);
        ExecuteScript("__gbSendUiMessage(__gbUiActor, __gbUiMessageName, __gbUiArgumentJson);");
    }
    public JsonArray GetBehaviorPropertyDefinitions(string behaviorUri)
    {
        SetScriptValue("__gbPropertyDefinitionsUri", behaviorUri);
        ExecuteScript("globalThis.__gbPropertyDefinitionsJson = JSON.stringify(getBehaviorProperties(__gbPropertyDefinitionsUri) || []);");
        return JsonNode.Parse(GetScriptString("__gbPropertyDefinitionsJson")) as JsonArray
            ?? throw new InvalidDataException($"Behavior property definitions for '{behaviorUri}' were not an array.");
    }

    public string? ValidateBehaviorSource(string behaviorUri, string source)
        => ValidateBehaviorSource(_engine, behaviorUri, source);

    public static string? ValidateBehaviorSourceWithoutRuntime(string behaviorUri, string source)
    {
        using var engine = new V8ScriptEngine();
        return ValidateBehaviorSource(engine, behaviorUri, source);
    }

    private static string? ValidateBehaviorSource(V8ScriptEngine engine, string behaviorUri, string source)
    {
        string wrappedSource = $"function __gbValidateBehavior() {{\n{BuildModuleBody(source)}\n}}";
        try
        {
            using V8Script compiled = engine.Compile(behaviorUri, wrappedSource);
            return null;
        }
        catch (ScriptEngineException exception)
        {
            string details = exception.ErrorDetails ?? exception.Message;
            int column = 0;
            int line = 0;
            string locationPrefix = behaviorUri + ":";
            int locationStart = details.IndexOf(locationPrefix, StringComparison.Ordinal);
            if (locationStart >= 0)
            {
                Match match = JavaScriptLineColumn.Match(details, locationStart + locationPrefix.Length);
                if (match.Success && int.TryParse(match.Groups["line"].Value, out int wrappedLine))
                {
                    int sourceLineCount = 1;
                    foreach (char character in source)
                    {
                        if (character == '\n')
                            sourceLineCount++;
                    }
                    line = Math.Clamp(wrappedLine - 1, 1, sourceLineCount);
                    if (int.TryParse(match.Groups["column"].Value, out int wrappedColumn))
                        column = wrappedColumn;
                }
            }

            string message = exception.Message;
            int newline = message.IndexOf('\n');
            if (newline >= 0)
                message = message[..newline].TrimEnd('\r');
            if (line <= 0)
                return message;
            return column > 0
                ? $"Line {line}: Column {column}: {message}"
                : $"Line {line}: {message}";
        }
    }

    public void Dispose() => _engine.Dispose();

    private bool ShouldContinueScript()
    {
        long deadline = System.Threading.Volatile.Read(ref _executionDeadlineTimestamp);
        return deadline == 0 || System.Diagnostics.Stopwatch.GetTimestamp() < deadline;
    }

    private void ExecuteScript(string source, string? sourceName = null, TimeSpan? timeBudget = null)
    {
        TimeSpan budget = timeBudget ?? TimeSpan.FromMilliseconds(250);
        long budgetTicks = (long)Math.Ceiling(budget.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + budgetTicks;
        System.Threading.Volatile.Write(ref _executionDeadlineTimestamp, deadline);
        try
        {
            if (sourceName is null)
                _engine.Execute(source);
            else
                _engine.Execute(sourceName, source);
        }
        finally
        {
            System.Threading.Volatile.Write(ref _executionDeadlineTimestamp, 0);
        }
    }

    private void SetScriptValue(string name, object value)
        => _engine.Script.SetProperty(name, value);

    private string GetScriptString(string name)
        => _engine.Script.GetProperty(name) as string
            ?? throw new InvalidDataException($"Behavior runtime global '{name}' was not a string.");

    private void RegisterHostBindings()
    {
        SetScriptValue("getActorString", new Func<int, int, string>(GetActorString));
        SetScriptValue("setActorString", new Action<int, int, string>(SetActorString));
        SetScriptValue("getActorBoolean", new Func<int, int, bool>(GetActorBoolean));
        SetScriptValue("setActorBoolean", new Action<int, int, bool>(SetActorBoolean));
        SetScriptValue("getActorFloat", new Func<int, int, double>(GetActorFloat));
        SetScriptValue("setActorFloat", new Action<int, int, double>(SetActorFloat));
        SetScriptValue("setActorVector3", new Action<int, int, double, double, double>(SetActorVector3));
        SetScriptValue("setActorQuaternion", new Action<int, int, double, double, double, double>(SetActorQuaternion));
        SetScriptValue("__gbReadActorVector3", new Func<int, int, string>(ReadActorVector3Json));
        SetScriptValue("__gbReadActorQuaternion", new Func<int, int, string>(ReadActorQuaternionJson));
        SetScriptValue("__gbService", new Func<string, string, string>(CallService));

        ExecuteScript(BridgeSource, "GBNG behavior bridge");
    }

    private const string BridgeSource = @"
const __gbBehaviorModules = new Map();
function getVoosModule(key) { return __gbBehaviorModules.get(key); }
function __gbRegisterModule(key, body) {
  const sourceUrl = String(key).replace(/[\r\n\u2028\u2029]/g, ""_"");
  const module = new Function(body + ""\n//# sourceURL="" + sourceUrl)();
  __gbBehaviorModules.set(key, module);
}
function callVoosService(name, args = {}) {
  return JSON.parse(__gbService(name, JSON.stringify(args)));
}
function __gbSendUiMessage(actorName, messageName, argumentJson) {
  moduleBehaviors_.sendMessage(actorName, messageName, JSON.parse(argumentJson), null);
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
  const collisionHandlingBrainIds = moduleBehaviors_.database.reset(database);
  globalThis.__gbCollisionBrainIdsJson = JSON.stringify(collisionHandlingBrainIds);
  initBehaviorsPending = true;
}
function __gbSyncActors(states) {
  const liveNames = new Set();
  const orderedNames = [];
  const inputMessages = [];
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
      aimDirection: new THREE.Vector3(state.aimDirection.x, state.aimDirection.y, state.aimDirection.z),
      aimOrigin: new THREE.Vector3(state.aimOrigin.x, state.aimOrigin.y, state.aimOrigin.z),
      lastAimHitPoint: new THREE.Vector3(state.lastAimHitPoint.x, state.lastAimHitPoint.y, state.lastAimHitPoint.z),
      aimingAtName: state.aimingAtName,
      lookAxes: new THREE.Vector3(state.lookAxes.x, state.lookAxes.y, state.lookAxes.z),
      hasInputState: state.hasInputState,
      inputState: state.inputState
    });
    if (state.messages) inputMessages.push(...state.messages);
  }
  for (const name of moduleBehaviors_.actors_.keys()) {
    if (!liveNames.has(name)) moduleBehaviors_.actors_.delete(name);
  }
  moduleBehaviors_.namesById_ = orderedNames;
  for (const message of inputMessages) {
    moduleBehaviors_.sendMessage(message.targetActorName, message.name, message.data || {}, null);
  }
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
    const memoryChanges = [];
    for (const actor of moduleBehaviors_.actors_.values()) {
      if (actor.memoryDirty_ && !actor.requestedDestroy) {
        memoryChanges.push({name: actor.name, memoryJson: actor.getMemoryJson()});
        actor.memoryDirty_ = false;
      }
      if (actor.requestedDestroy) {
        memoryChanges.push({name: actor.name, requestedDestroy: true});
      }
    }
    globalThis.__gbMemoryJson = JSON.stringify(memoryChanges);
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
            string text = Godot.FileAccess.GetFileAsString(sourcePath);
            if (Path.GetFileName(sourcePath) == "debug.js.txt")
            {
                int testIndex = text.IndexOf("\nrunUnitTests('debug'", StringComparison.Ordinal);
                if (testIndex >= 0)
                    text = text[..testIndex];
            }
            source.AppendLine(text);
        }
        source.AppendLine(@"
getCallStack = function () {
  const lines = (new Error().stack || '').split('\n');
  const callFrame = lines.findIndex(line => line.includes('getCallStack'));
  return callFrame < 0 ? lines.join('\n') : lines.slice(callFrame + 1).join('\n');
};
");

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
        HashSet<string> requiredUris = GetRequiredBuiltInUris();
        var pathsByUri = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string path in EnumerateFiles(BuiltinBehaviorRoot, ".js.txt"))
        {
            if (path.Contains("/Specs/", StringComparison.Ordinal))
                continue;
            string moduleName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
            string uri = $"builtin:{moduleName}";
            if (!requiredUris.Contains(uri))
                continue;
            if (!pathsByUri.TryGetValue(uri, out List<string>? paths))
                pathsByUri.Add(uri, paths = new List<string>());
            paths.Add(path);
        }

        foreach (string uri in requiredUris.Order(StringComparer.Ordinal))
        {
            if (!pathsByUri.TryGetValue(uri, out List<string>? paths))
                throw new FileNotFoundException($"No built-in behavior module exists for '{uri}'.");
            if (paths.Count != 1)
                throw new InvalidDataException($"Built-in behavior module '{uri}' is ambiguous: {string.Join(", ", paths)}.");
            RegisterModule(uri, Godot.FileAccess.GetFileAsString(paths[0]));
        }
    }

    private HashSet<string> GetRequiredBuiltInUris()
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (_document.BehaviorDatabase?["brains"] is not JsonArray brains)
            return required;

        foreach (JsonNode? brain in brains)
        {
            if (brain?["behaviorUses"] is not JsonArray uses)
                continue;
            foreach (JsonNode? use in uses)
            {
                string uri = LegacySaveDocument.ReadString(use?["behaviorUri"]);
                if (uri.StartsWith("builtin:", StringComparison.Ordinal))
                    required.Add(uri);
            }
        }

        return required;
    }

    private void RegisterEmbeddedModules()
    {
        JsonObject? database = _document.BehaviorDatabase;
        if (database?["behaviorIds"] is not JsonArray ids || database["behaviors"] is not JsonArray behaviors)
            return;


        int count = Math.Min(ids.Count, behaviors.Count);
        for (int i = 0; i < count; i++)
        {
            string id = LegacySaveDocument.ReadString(ids[i]);
            if (id.Length == 0 || behaviors[i] is not JsonObject behavior)
                continue;
            string source = LegacySaveDocument.ReadString(behavior["javascript"]);
            if (source.Length > 0)
                RegisterModule($"embedded:{id}", source);
        }
    }

    private void RegisterModule(string uri, string source)
    {
        SetScriptValue("__gbModuleKey", uri);
        SetScriptValue("__gbModuleBody", BuildModuleBody(source));
        ExecuteScript("__gbRegisterModule(__gbModuleKey, __gbModuleBody);");
    }

    private static string BuildModuleBody(string source)
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
        return body + $"\nreturn {{{exportObject}}};\n";
    }

    private void ConfigureBehaviorDatabase()
    {
        JsonObject? database = _document.BehaviorDatabase;
        if (database is null)
            return;

        SetScriptValue("__gbDatabaseJson", database.ToJsonString());
        ExecuteScript("__gbInstallDatabase(__gbDatabaseJson);");
        _collisionHandlingBrainIds.Clear();
        if (JsonNode.Parse(GetScriptString("__gbCollisionBrainIdsJson")) is JsonArray brainIds)
        {
            foreach (JsonNode? brainId in brainIds)
            {
                string id = LegacySaveDocument.ReadString(brainId);
                if (id.Length > 0)
                    _collisionHandlingBrainIds.Add(id);
            }
        }
    }

    private void SyncActors()
    {
        _actorsByName.Clear();
        _actorsById.Clear();
        List<object> states = _actorStatesBuffer;
        states.Clear();
        bool hasInputFrame = _collectInput;
        bool mouseIsDown = hasInputFrame && Input.IsMouseButtonPressed(MouseButton.Left) && !_mouseOverUi;
        bool secondaryActionDown = hasInputFrame && Input.IsMouseButtonPressed(MouseButton.Right) && !_mouseOverUi;
        bool mouseJustPressed = mouseIsDown && !_reportedMouseDown;
        bool mouseJustReleased = !mouseIsDown && _reportedMouseDown;
        List<string> keysHeld = _keysHeldBuffer;
        List<string> keysJustPressed = _keysJustPressedBuffer;
        List<string> keysJustReleased = _keysJustReleasedBuffer;
        keysHeld.Clear();
        keysJustPressed.Clear();
        keysJustReleased.Clear();
        if (hasInputFrame)
        {
            foreach (string keyName in ScriptableKeyNames)
            {
                bool isHeld = IsScriptableKeyHeld(keyName, mouseIsDown, secondaryActionDown);
                bool wasHeld = _keysHeldLastTick.Contains(keyName);
                if (isHeld)
                {
                    keysHeld.Add(keyName);
                    if (!wasHeld)
                        keysJustPressed.Add(keyName);
                }
                else if (wasHeld)
                {
                    keysJustReleased.Add(keyName);
                }
            }
        }
        List<LegacyBehaviorMessage> pendingBehaviorMessages = _pendingBehaviorMessagesBuffer;

        Vector2 viewportSize = _camera.GetViewport().GetVisibleRect().Size;
        float uiScale = viewportSize.X / LegacyGameUiWidth;
        Vector2 mouseUiPosition = uiScale > 0f ? _mousePosition / uiScale : Vector2.Zero;
        Vector3 localMouseRayOrigin = _camera.ProjectRayOrigin(_mousePosition);
        Vector3 localMouseRayDirection = _camera.ProjectRayNormal(_mousePosition);

        int playerSlotNumber = 0;
        bool networkHost = _lanSession?.IsHost == true;
        foreach (WorldActorNode actor in _actors)
        {
            _actorsByName.Add(actor.LegacyName, actor);
            actor.SetLegacyCollisionHandling(_collisionHandlingBrainIds.Contains(actor.State.BrainName));
            _actorsById.Add(actor);
            bool isPlayer = actor.State.IsPlayerControllable;
            int playerSlot = isPlayer ? ++playerSlotNumber : 0;
            bool remoteControlled = isPlayer && networkHost && playerSlot > 1;
            LegacyLanInputFrame? remoteInput = remoteControlled ? _lanSession!.GetRemoteInputForSlot(playerSlot) : null;
            RemoteInputHistory? remoteHistory = hasInputFrame && remoteControlled
                ? UpdateRemoteInputHistory(actor.LegacyName, remoteInput)
                : null;
            bool hasPlayerInput = isPlayer && hasInputFrame;
            List<string> actorKeysHeld = remoteControlled ? remoteHistory?.KeysHeld ?? EmptyInputKeys : keysHeld;
            List<string> actorKeysJustPressed = remoteControlled ? remoteHistory?.KeysJustPressed ?? EmptyInputKeys : keysJustPressed;
            List<string> actorKeysJustReleased = remoteControlled ? remoteHistory?.KeysJustReleased ?? EmptyInputKeys : keysJustReleased;
            bool actorMouseDown = remoteControlled ? remoteHistory?.MouseDown ?? false : mouseIsDown;
            bool actorSecondaryDown = remoteControlled ? remoteHistory?.SecondaryActionDown ?? false : secondaryActionDown;
            bool actorMouseJustPressed = remoteControlled ? remoteHistory?.MouseJustPressed ?? false : mouseJustPressed;
            bool actorMouseJustReleased = remoteControlled ? remoteHistory?.MouseJustReleased ?? false : mouseJustReleased;
            float actorMouseWheelRaw = remoteControlled ? remoteHistory?.MouseWheelRaw ?? 0f : _mouseWheelRaw;
            Vector2 actorMouseUiPosition = remoteControlled
                ? remoteInput is null ? Vector2.Zero : new Vector2(remoteInput.MouseUiX, remoteInput.MouseUiY)
                : mouseUiPosition;
            Vector2 actorLookAxes = remoteControlled
                ? remoteInput is null ? Vector2.Zero : new Vector2(remoteInput.LookX, remoteInput.LookY)
                : _lookAxes;
            Vector3 actorMouseRayOrigin = remoteControlled
                ? remoteInput is null ? Vector3.Zero : new Vector3(remoteInput.RayOriginX, remoteInput.RayOriginY, remoteInput.RayOriginZ)
                : localMouseRayOrigin;
            Vector3 actorMouseRayDirection = remoteControlled
                ? remoteInput is null ? Vector3.Zero : new Vector3(remoteInput.RayDirectionX, remoteInput.RayDirectionY, remoteInput.RayDirectionZ)
                : localMouseRayDirection;
            Basis actorCameraBasis = remoteInput is null
                ? remoteControlled ? actor.GlobalBasis : _camera.GlobalBasis
                : new Basis(new Quaternion(
                    remoteInput.CameraRotationX,
                    remoteInput.CameraRotationY,
                    remoteInput.CameraRotationZ,
                    remoteInput.CameraRotationW));
            Vector3 actorCameraPosition = remoteInput is null
                ? remoteControlled ? actor.GlobalPosition : _camera.GlobalPosition
                : new Vector3(remoteInput.CameraPositionX, remoteInput.CameraPositionY, remoteInput.CameraPositionZ);
            Vector3 playerInput = !isPlayer
                ? Vector3.Zero
                : remoteControlled
                    ? remoteInput is null ? Vector3.Zero : ReadRemotePlayerThrottle(remoteHistory!.HeldKeys)
                    : ReadPlayerThrottle();
            Vector3 worldThrottleGodot = actorCameraBasis * CoordinateSpace.ToGodot(playerInput);
            Vector3 worldThrottle = CoordinateSpace.ToLegacy(worldThrottleGodot.Normalized());
            Vector3 spawnPosition = actor.State.ReadLegacyVector("spawnPosition", actor.State.ReadLegacyVector("position", Vector3.Zero));
            Quaternion spawnRotation = actor.State.ReadLegacyQuaternion("spawnRotation");
            Vector3 aimDirectionGodot = isPlayer ? -actorCameraBasis.Z : -actor.GlobalBasis.Z;
            Vector3 aimOriginGodot = isPlayer ? actorCameraPosition : actor.GlobalPosition + new Vector3(0f, 0.5f, 0f);
            Vector3 lastAimHitPoint = Vector3.Zero;
            string? aimingAtName = null;
            bool aimingEnabled = isPlayer && LegacySaveDocument.ReadBool(actor.State.Data["enableAiming"]);
            if (aimingEnabled && _physicsQueryAvailable)
            {
                var query = PhysicsRayQueryParameters3D.Create(
                    aimOriginGodot,
                    aimOriginGodot + aimDirectionGodot * 99f);
                query.CollisionMask = ActorCollisionLayer | ActorTriggerCollisionLayer | TerrainCollisionLayer;
                query.CollideWithAreas = true;
                query.CollideWithBodies = true;
                query.Exclude = new Godot.Collections.Array<Rid> { actor.GetRid() };
                Godot.Collections.Dictionary hit = _world.GetWorld3D().DirectSpaceState.IntersectRay(query);
                if (hit.Count > 0)
                {
                    lastAimHitPoint = CoordinateSpace.ToLegacy(hit["position"].AsVector3());
                    WorldActorNode? target = FindActorFromHit(hit);
                    if (target != actor)
                        aimingAtName = target?.LegacyName;
                }
            }

            Vector3 legacyMouseRayOrigin = CoordinateSpace.ToLegacy(actorMouseRayOrigin);
            Vector3 legacyMouseRayDirection = CoordinateSpace.ToLegacy(actorMouseRayDirection);
            List<object>? messages = hasPlayerInput
                ? BuildInputMessages(
                    actor.LegacyName,
                    actorKeysHeld,
                    actorKeysJustPressed,
                    actorKeysJustReleased,
                    actorMouseJustPressed,
                    actorMouseJustReleased,
                    actorMouseDown,
                    actorMouseRayOrigin,
                    actorMouseRayDirection)
                : null;
            pendingBehaviorMessages.Clear();
            actor.DrainPendingBehaviorMessages(pendingBehaviorMessages);
            foreach (LegacyBehaviorMessage pendingMessage in pendingBehaviorMessages)
            {
                messages ??= new List<object>(pendingBehaviorMessages.Count);
                messages.Add(new
                {
                    targetActorName = actor.LegacyName,
                    name = pendingMessage.Name,
                    data = pendingMessage.Name == "TerrainCollision"
                        ? (object)new { blockStyle = pendingMessage.BlockStyle }
                        : new { other = pendingMessage.OtherActorName }
                });
            }
            states.Add(new
            {
                name = actor.LegacyName,
                brainName = actor.State.BrainName,
                memoryJson = _actorsKnownToRuntime.Contains(actor.LegacyName) ? null : actor.State.MemoryJson,
                preferOffstage = actor.State.IsOffstage,
                transformParent = actor.State.TransformParent,
                spawnPosition = ToVectorJson(spawnPosition),
                spawnRotation = ToQuaternionJson(spawnRotation),
                isSprinting = isPlayer && (remoteInput?.Sprinting ?? (!remoteControlled && Input.IsKeyPressed(Key.Shift))),
                worldSpaceThrottle = ToVectorJson(worldThrottle),
                inputThrottle = ToVectorJson(playerInput),
                aimDirection = ToVectorJson(CoordinateSpace.ToLegacy(aimDirectionGodot.Normalized())),
                aimOrigin = ToVectorJson(CoordinateSpace.ToLegacy(aimOriginGodot)),
                lastAimHitPoint = ToVectorJson(lastAimHitPoint),
                aimingAtName,
                lookAxes = ToVectorJson(isPlayer ? new Vector3(actorLookAxes.X, actorLookAxes.Y, 0f) : Vector3.Zero),
                hasInputState = hasPlayerInput,
                inputState = hasPlayerInput
                    ? new
                    {
                        mouseX = actorMouseUiPosition.X,
                        mouseY = actorMouseUiPosition.Y,
                        mouseButtonIsPressed = actorMouseDown,
                        mouseButtonJustPressed = actorMouseJustPressed,
                        mouseButtonJustReleased = actorMouseJustReleased,
                        mouseRayOrigin = ToVectorJson(legacyMouseRayOrigin),
                        mouseRayDirection = ToVectorJson(legacyMouseRayDirection),
                        mouseWheelRaw = actorMouseWheelRaw,
                        mouseWheel = actorMouseWheelRaw * (OperatingSystem.IsMacOS() ? 0.033f : 1f),
                        keysJustPressed = actorKeysJustPressed,
                        keysHeld = actorKeysHeld,
                        keysJustReleased = actorKeysJustReleased
                    }
                    : null,
                messages = (object?)messages ?? Array.Empty<object>()
            });
        }

        SetScriptValue("__gbActorStates", JsonSerializer.Serialize(states));
        states.Clear();
        ExecuteScript("__gbActorStatesJson(__gbActorStates);");
        _remoteInputHistoryRemoveBuffer.Clear();
        foreach (string actorName in _remoteInputHistories.Keys)
        {
            if (!_actorsByName.ContainsKey(actorName))
                _remoteInputHistoryRemoveBuffer.Add(actorName);
        }
        foreach (string actorName in _remoteInputHistoryRemoveBuffer)
            _remoteInputHistories.Remove(actorName);
        if (hasInputFrame)
        {
            _keysHeldLastTick.Clear();
            _keysHeldLastTick.UnionWith(keysHeld);
            _reportedMouseDown = mouseIsDown;
        }
        _lookAxes = Vector2.Zero;
        _mouseWheelRaw = 0f;
    }
    private RemoteInputHistory UpdateRemoteInputHistory(string actorName, LegacyLanInputFrame? frame)
    {
        if (!_remoteInputHistories.TryGetValue(actorName, out RemoteInputHistory? history))
        {
            history = new RemoteInputHistory();
            _remoteInputHistories.Add(actorName, history);
        }
        history.KeysHeld.Clear();
        history.HeldKeys.Clear();
        history.KeysJustPressed.Clear();
        history.KeysJustReleased.Clear();
        if (frame is not null)
        {
            foreach (string key in frame.KeysHeld)
            {
                history.KeysHeld.Add(key);
                history.HeldKeys.Add(key);
                if (!history.PreviousKeysHeld.Contains(key))
                    history.KeysJustPressed.Add(key);
            }
        }
        foreach (string key in history.PreviousKeysHeld)
        {
            if (!history.HeldKeys.Contains(key))
                history.KeysJustReleased.Add(key);
        }
        history.PreviousKeysHeld.Clear();
        history.PreviousKeysHeld.UnionWith(history.HeldKeys);
        bool mouseDown = frame?.MouseDown ?? false;
        history.MouseJustPressed = mouseDown && !history.MouseDown;
        history.MouseJustReleased = !mouseDown && history.MouseDown;
        history.MouseDown = mouseDown;
        history.SecondaryActionDown = frame?.SecondaryActionDown ?? false;
        history.MouseWheelRaw = frame is not null && frame.Sequence != history.LastSequence ? frame.MouseWheelRaw : 0f;
        if (frame is not null)
            history.LastSequence = frame.Sequence;
        return history;
    }

    private static readonly List<string> EmptyInputKeys = new();

    private sealed class RemoteInputHistory
    {
        public HashSet<string> PreviousKeysHeld { get; } = new(StringComparer.Ordinal);
        public HashSet<string> HeldKeys { get; } = new(StringComparer.Ordinal);
        public List<string> KeysHeld { get; } = new();
        public List<string> KeysJustPressed { get; } = new();
        public List<string> KeysJustReleased { get; } = new();
        public bool MouseDown { get; set; }
        public bool SecondaryActionDown { get; set; }
        public bool MouseJustPressed { get; set; }
        public bool MouseJustReleased { get; set; }
        public float MouseWheelRaw { get; set; }
        public long LastSequence { get; set; }
    }

    private static Vector3 ReadRemotePlayerThrottle(HashSet<string> heldKeys)
    {
        float horizontal = (heldKeys.Contains("d") || heldKeys.Contains("right") ? 1f : 0f)
            - (heldKeys.Contains("a") || heldKeys.Contains("left") ? 1f : 0f);
        float vertical = (heldKeys.Contains("space") ? 1f : 0f) - (heldKeys.Contains("c") ? 1f : 0f);
        float depth = (heldKeys.Contains("w") || heldKeys.Contains("up") ? 1f : 0f)
            - (heldKeys.Contains("s") || heldKeys.Contains("down") ? 1f : 0f);
        return new Vector3(horizontal, vertical, depth);
    }

    private List<object> BuildInputMessages(
        string actorName,
        List<string> keysHeld,
        List<string> keysJustPressed,
        List<string> keysJustReleased,
        bool mouseJustPressed,
        bool mouseJustReleased,
        bool mouseIsDown,
        Vector3 mouseRayOrigin,
        Vector3 mouseRayDirection)
    {
        var messages = new List<object>();
        if (keysJustPressed.Contains("@jump"))
        {
            AddInputMessage(messages, actorName, "JumpTriggered");
            AddInputMessage(messages, actorName, "Jump");
        }
        if (keysJustPressed.Contains("@pri"))
        {
            AddInputMessage(messages, actorName, "Action1Triggered");
            AddInputMessage(messages, actorName, "PrimaryAction");
        }
        if (keysJustPressed.Contains("@sec"))
        {
            AddInputMessage(messages, actorName, "Action2Triggered");
            AddInputMessage(messages, actorName, "SecondaryAction");
        }
        foreach (string keyName in ScriptableKeyNames)
        {
            var data = new { keyName };
            if (keysJustPressed.Contains(keyName))
                AddInputMessage(messages, actorName, "KeyDown", data);
            if (keysHeld.Contains(keyName))
                AddInputMessage(messages, actorName, "KeyHeld", data);
            if (keysJustReleased.Contains(keyName))
                AddInputMessage(messages, actorName, "KeyUp", data);
        }
        if (mouseJustPressed)
            AddInputMessage(messages, actorName, "MouseDown");
        if (mouseIsDown)
            AddInputMessage(messages, actorName, "MouseHeld");
        if (mouseJustReleased)
            AddInputMessage(messages, actorName, "MouseUp");
        if (mouseJustPressed)
        {
            WorldActorNode? clickedActor = RaycastActorUnderMouse(mouseRayOrigin, mouseRayDirection);
            if (clickedActor is not null)
                AddInputMessage(messages, clickedActor.LegacyName, "ActorClicked");
        }
        return messages;
    }

    private static void AddInputMessage(List<object> messages, string targetActorName, string messageName, object? data = null)
    {
        messages.Add(new
        {
            targetActorName,
            name = messageName,
            data = data ?? new object()
        });
    }

    private WorldActorNode? RaycastActorUnderMouse(Vector3 origin, Vector3 direction)
    {
        var query = PhysicsRayQueryParameters3D.Create(origin, origin + direction * 1000f);
        query.CollisionMask = ActorCollisionLayer | ActorTriggerCollisionLayer;
        query.CollideWithAreas = true;
        query.CollideWithBodies = true;
        Godot.Collections.Dictionary hit = _world.GetWorld3D().DirectSpaceState.IntersectRay(query);
        return hit.Count > 0 ? FindActorFromHit(hit) : null;
    }

    private static bool IsScriptableKeyHeld(string keyName, bool mouseIsDown, bool secondaryActionDown)
        => LegacyInputKeys.IsHeld(keyName, mouseIsDown, secondaryActionDown);

    private Vector3 ReadPlayerThrottle()
    {
        float horizontal = (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right) ? 1f : 0f)
            - (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left) ? 1f : 0f);
        float vertical = (Input.IsKeyPressed(Key.Space) ? 1f : 0f) - (Input.IsKeyPressed(Key.C) ? 1f : 0f);
        float depth = (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up) ? 1f : 0f)
            - (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down) ? 1f : 0f);
        return new Vector3(horizontal, vertical, depth);
    }

    private void ApplyRuntimeResponse(double deltaSeconds)
    {
        string responseJson = GetScriptString("__gbResponseJson");
        if (JsonNode.Parse(responseJson) is JsonObject response)
        {
            if (response["playerToolTips"] is JsonArray playerToolTips && !JsonNode.DeepEquals(playerToolTips, _lastPlayerToolTips))
            {
                _lastPlayerToolTips = (JsonArray)playerToolTips.DeepClone();
                PlayerToolTipsUpdated?.Invoke(playerToolTips);
            }
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

        string memoryJson = GetScriptString("__gbMemoryJson");
        if (JsonNode.Parse(memoryJson) is JsonArray memories)
        {
            foreach (JsonNode? item in memories)
            {
                if (item is not JsonObject actorMemory)
                    continue;
                string actorName = LegacySaveDocument.ReadString(actorMemory["name"]);
                WorldActorNode? actor = FindActor(actorName);
                if (actor is null)
                    continue;
                if (actorMemory["memoryJson"] is JsonValue)
                    actor.State.Data["memoryJson"] = LegacySaveDocument.ReadString(actorMemory["memoryJson"], "{}");
                if (LegacySaveDocument.ReadBool(actorMemory["requestedDestroy"]))
                    _actorsToDestroy.Add(actorName);
            }
        }
        foreach (WorldActorNode actor in _actors)
            actor.ApplyDesiredVelocity(deltaSeconds);
        DestroyPendingActors();
        _terrain?.FlushUpdates();

    }
    private void DestroyPendingActors()
    {
        if (_actorsToDestroy.Count == 0)
            return;

        bool changed = false;
        foreach (string actorName in _actorsToDestroy)
        {
            WorldActorNode? actor = FindActor(actorName);
            if (actor is null || actor.LegacyName.StartsWith("builtin:", StringComparison.InvariantCulture))
                continue;

            foreach (WorldActorNode child in _actors.Where(candidate => candidate.State.TransformParent == actorName).ToArray())
            {
                Transform3D worldTransform = child.GlobalTransform;
                child.Reparent(_world, keepGlobalTransform: false);
                child.GlobalTransform = worldTransform;
            }
            _document.RemoveActor(actorName);
            _actors.Remove(actor);
            actor.QueueFree();
            changed = true;
        }
        _actorsToDestroy.Clear();
        if (changed)
        {
            SyncActors();
            _actorsChanged?.Invoke();
        }
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
            case 4: actor.State.WriteString("debugString", value); break;
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
            case 11:
                actor.UseDesiredVelocity = value;
                actor.State.WriteBool("useDesiredVelocity", value);
                break;
            case 12:
                actor.IgnoreVerticalDesiredVelocity = value;
                actor.State.WriteBool("ignoreVerticalDesiredVelocity", value);
                break;
            case 18:
                actor.UseStickyDesiredVelocity = value;
                actor.State.WriteBool("useStickyDesiredVelocity", value);
                break;
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
            case 5:
                actor.DesiredVelocity = godotValue;
                actor.State.WriteLegacyVector("desiredVelocity", legacyValue);
                break;
            case 6:
                actor.LinearVelocity = godotValue;
                actor.State.SetVelocity(godotValue);
                break;
            case 7:
                actor.AngularVelocity = godotValue;
                actor.State.SetAngularVelocity(godotValue);
                break;
            case 8:
                actor.StickyDesiredVelocity = godotValue;
                actor.State.WriteLegacyVector("stickyDesiredVelocity", legacyValue);
                break;
            case 9:
                actor.StickyForce = godotValue;
                actor.State.WriteLegacyVector("stickyForce", legacyValue);
                break;
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
            7 => actor.GetWorldRenderBounds().GetCenter(),
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
                int fieldId = LegacySaveDocument.ReadInt(args["fieldId"], 0);
                return JsonSerializer.Serialize(actor is not null && fieldId == 0 ? ToColorJson(actor.State.Tint) : ToColorJson(Colors.White));
            }
            case "SetActorColorField":
            {
                WorldActorNode? actor = GetActor(LegacySaveDocument.ReadInt(args["actorId"], -1));
                int fieldId = LegacySaveDocument.ReadInt(args["fieldId"], 0);
                if (actor is not null && fieldId == 0 && args["newValue"] is JsonObject color)
                {
                    actor.SetTint(new Color(
                        LegacySaveDocument.ReadFloat(color["r"], 1f),
                        LegacySaveDocument.ReadFloat(color["g"], 1f),
                        LegacySaveDocument.ReadFloat(color["b"], 1f),
                        LegacySaveDocument.ReadFloat(color["a"], 1f)));
                }
                return "true";
            }
            case "GetPlayerActors":
                return JsonSerializer.Serialize(_actors.Where(actor => actor.State.IsPlayerControllable).Select(actor => actor.LegacyName));
            case "GetPlayerControlledActor":
                return JsonSerializer.Serialize(GetLocalPlayerActor()?.LegacyName);
            case "GetCameraActor":
            {
                WorldActorNode? player = GetLocalPlayerActor();
                string cameraName = player is null ? "" : LegacySaveDocument.ReadString(player.State.Data["cameraActor"]);
                return JsonSerializer.Serialize(_actors.FirstOrDefault(actor => actor.LegacyName == cameraName)?.LegacyName);
            }
            case "GetPlayersInfo":
                if (_lanSession is not null)
                {
                    _lanSession.TryGetPlayerInfo(out string playerInfo, out _);
                    return playerInfo;
                }
                return JsonSerializer.Serialize(new
                {
                    allPlayers = new[] { new { id = "local", slotNumber = 1, nickName = "Local Player" } },
                    localPlayerId = "local"
                });
            case "IsMultiplayer":
                return (_lanSession?.IsMultiplayer ?? false).ToString().ToLowerInvariant();
            case "IsMasterClient":
                return (_lanSession?.IsMaster ?? true).ToString().ToLowerInvariant();
            case "GetScreenInfo":
            {
                Vector2 viewportSize = _camera.GetViewport().GetVisibleRect().Size;
                int uiHeight = viewportSize.X > 0f ? (int)Math.Ceiling(viewportSize.Y * 1600f / viewportSize.X) : 900;
                return JsonSerializer.Serialize(new
                {
                    width = 1600,
                    height = uiHeight,
                    pixelWidth = (int)viewportSize.X,
                    pixelHeight = (int)viewportSize.Y
                });
            }
            case "GetCameraInfo":
                return JsonSerializer.Serialize(new
                {
                    pos = ToVectorJson(CoordinateSpace.ToLegacy(_camera.GlobalPosition)),
                    rot = ToQuaternionJson(CoordinateSpace.ToLegacy(_camera.GlobalBasis.GetRotationQuaternion()))
                });
            case "OverlapSphere":
                return OverlapSphere(args);
            case "RequestUi":
                _requestUi(args["commands"] as JsonArray ?? new JsonArray());
                return "true";
            case "ReportBehaviorException":
            {
                int generatedLineNumber = LegacySaveDocument.ReadInt(args["lineNum"], -1);
                int editorLineNumber = generatedLineNumber;
                string message = LegacySaveDocument.ReadString(args["message"]);
                if (generatedLineNumber > 0)
                {
                    editorLineNumber = Math.Max(1, generatedLineNumber - FunctionConstructorLineOffset);
                    message = BehaviorStackLineColumn.Replace(message, match =>
                    {
                        if (!int.TryParse(match.Groups["line"].Value, out int stackLine)
                            || stackLine <= FunctionConstructorLineOffset)
                            return match.Value;
                        return $"line {stackLine - FunctionConstructorLineOffset}:{match.Groups["column"].Value}";
                    });
                }
                args["lineNum"] = editorLineNumber;
                args["message"] = message;
                _reportBehaviorException?.Invoke(editorLineNumber, message);
                GD.PushError($"Behavior runtime exception: {args.ToJsonString()}");
                return "true";
            }
            case "LogBehaviorMessage":
                GD.Print(argsJson);
                return "true";
            case "BeginProfileSample":
            case "EndProfileSample":
                return "0";
            case "CheckBox":
                return CheckBox(args);
            case "Cast":
                return Cast(args);
            case "SetTerrainCell":
                return SetTerrainCell(args);
            case "GetTerrainCell":
                return GetTerrainCell(args);
            case "CloneActor":
                return CloneActor(args);
            case "InstantiatePrefab":
                return InstantiatePrefab(args);
            case "DestroyActors":
                QueueActorDestruction(args["actorNames"] as JsonArray);
                return "true";
            case "PlayOneShotAnimation":
            {
                WorldActorNode? actor = GetActor(LegacySaveDocument.ReadInt(args["actorTempId"], -1));
                actor?.PlayOneShotAnimation(LegacySaveDocument.ReadString(args["animationName"]));
                return "true";
            }
            case "TransferPlayerControl":
                return TransferPlayerControl(args);
            case "RequestTempCameraOffset":
                return RequestCameraOffset(args);
            case "ProjectPoint":
                return ProjectPoint(args);
            case "ProjectSphere":
                return ProjectSphere(args);
            case "GetActorScreenRect":
                return GetActorScreenRect(args);
            case "PlaySound":
                return PlaySound(args);
            case "SpawnParticleEffect":
                return SpawnParticleEffect(args);
            case "SetSkyType":
                return SetSkyType(args);
            case "GetSkyType":
                return JsonSerializer.Serialize(new { value = LegacySaveDocument.ReadString(_document.Stage?["skyType"], "Day") });
            case "SetSkyColor":
                return SetSkyColor(args);
            case "GetSkyColor":
                return JsonSerializer.Serialize(new { value = ReadStageColor("skyColor") });
            case "SetSceneLighting":
                return SetSceneLighting(args);
            case "GetSceneLighting":
                return JsonSerializer.Serialize(new { value = LegacySaveDocument.ReadString(_document.Stage?["sceneLightingMode"], "DAY").ToUpperInvariant() });
            default:
                throw new NotSupportedException($"Unknown legacy behavior service '{serviceName}'.");
    }
    }

    private bool ActorHasTag(WorldActorNode actor, string tag)
    {
        return actor.State.Data["tags"] is JsonArray tags && tags.Any(value => LegacySaveDocument.ReadString(value) == tag);
    }


    private WorldActorNode? GetActor(int actorId)
        => (uint)actorId < (uint)_actorsById.Count ? _actorsById[actorId] : null;

    private WorldActorNode? FindActor(string name)
        => _actorsByName.TryGetValue(name, out WorldActorNode? actor) ? actor : null;

    internal static string[] EnumerateFiles(string root, string suffix)
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
