using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace GBNG.Domain;

public sealed class LegacySaveDocument
{
    public const int CurrentLegacySaveVersion = 7;

    private readonly JsonObject _root;
    private readonly List<LegacyActorData> _actors;

    private LegacySaveDocument(JsonObject root, string sourcePath)
    {
        _root = root;
        SourcePath = sourcePath;
        Version = ReadInt(root["version"], 0);

        if (Version < 1)
            throw new InvalidDataException("The .voos file has no valid save version.");
        if (Version > CurrentLegacySaveVersion)
            throw new NotSupportedException($"The .voos save version {Version} is newer than this port supports ({CurrentLegacySaveVersion}).");

        if (root["voosEngineState"] is not JsonObject engineState || engineState["actors"] is not JsonArray actors)
            throw new InvalidDataException("The .voos file is missing voosEngineState.actors.");

        _actors = new List<LegacyActorData>(actors.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? actorNode in actors)
        {
            if (actorNode is not JsonObject actor)
                throw new InvalidDataException("The .voos actor list contains a non-object value.");

            var state = new LegacyActorData(actor);
            if (string.IsNullOrWhiteSpace(state.Name))
                throw new InvalidDataException("The .voos actor list contains an actor with no name.");
            if (!names.Add(state.Name))
                throw new InvalidDataException($"The .voos actor list contains duplicate actor name '{state.Name}'.");

            _actors.Add(state);
        }
    }

    public string SourcePath { get; private set; }
    public int Version { get; }
    public JsonObject Root => _root;
    public JsonObject EngineState => (JsonObject)_root["voosEngineState"]!;
    public JsonObject? Stage => _root["stage"] as JsonObject;
    public JsonObject? GridState => _root["gridState"] as JsonObject;
    public JsonObject? BehaviorDatabase => _root["behaviorDatabase"] as JsonObject;
    public IReadOnlyList<LegacyActorData> Actors => _actors;

    public static LegacySaveDocument Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A .voos path is required.", nameof(path));

        string json = Godot.FileAccess.GetFileAsString(path);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException($"The .voos file is missing or empty: {path}");

        JsonNode? parsed = JsonNode.Parse(json);
        if (parsed is not JsonObject root)
            throw new InvalidDataException("The .voos document root must be a JSON object.");

        return new LegacySaveDocument(root, path);
    }

    public void SaveTerrainPayload(string encodedTerrain)
    {
        JsonObject gridState = EnsureObject(_root, "gridState");
        gridState["v2Data"] = encodedTerrain;
    }

    public void SaveActorTransforms(IReadOnlyDictionary<string, Transform3D> transforms)
    {
        foreach (LegacyActorData actor in _actors)
        {
            if (transforms.TryGetValue(actor.Name, out Transform3D transform))
                actor.SetWorldTransform(transform);
        }
    }

    public LegacyActorData AddActor(JsonObject data) => InsertActor(data, _actors.Count);

    public LegacyActorData InsertActor(JsonObject data, int index)
    {
        var actor = new LegacyActorData(data);
        if (string.IsNullOrWhiteSpace(actor.Name) || _actors.Any(existing => existing.Name == actor.Name))
            throw new InvalidDataException($"Cannot add actor with missing or duplicate name '{actor.Name}'.");

        int insertIndex = Math.Clamp(index, 0, _actors.Count);
        ((JsonArray)EngineState["actors"]!).Insert(insertIndex, data);
        _actors.Insert(insertIndex, actor);
        return actor;
    }

    public bool RemoveActor(string name)
    {
        int index = _actors.FindIndex(actor => actor.Name == name);
        if (index < 0)
            return false;

        _actors.RemoveAt(index);
        JsonArray actorArray = (JsonArray)EngineState["actors"]!;
        actorArray.RemoveAt(index);
        foreach (LegacyActorData child in _actors.Where(actor => actor.TransformParent == name))
            child.SetTransformParent(string.Empty);
        return true;
    }

    public void SaveTo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A destination .voos path is required.", nameof(path));
        if (path.StartsWith("res://", StringComparison.Ordinal))
            throw new ArgumentException("Game saves cannot overwrite read-only project resources.", nameof(path));

        string json = _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        string absolutePath = ProjectSettings.GlobalizePath(path);
        string? destinationDirectory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        string temporaryPath = absolutePath + ".tmp";
        File.WriteAllText(temporaryPath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, absolutePath, overwrite: true);
        SourcePath = path;
    }



    internal static JsonObject EnsureObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject current)
            return current;

        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    internal static float ReadFloat(JsonNode? node, float fallback)
    {
        if (node is null)
            return fallback;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<float>(out float single))
                return single;
            if (value.TryGetValue<double>(out double number))
                return (float)number;
            if (value.TryGetValue<int>(out int integer))
                return integer;
            if (value.TryGetValue<long>(out long longInteger))
                return longInteger;
        }

        throw new InvalidDataException("Expected a numeric value in the .voos save.");
    }

    internal static int ReadInt(JsonNode? node, int fallback)
    {
        if (node is null)
            return fallback;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out int integer))
                return integer;
            if (value.TryGetValue<long>(out long longInteger))
                return checked((int)longInteger);
        }

        throw new InvalidDataException("Expected an integer value in the .voos save.");
    }

    internal static string ReadString(JsonNode? node, string fallback = "")
    {
        if (node is null)
            return fallback;
        if (node is JsonValue value && value.TryGetValue<string>(out string? text))
            return text ?? fallback;

        throw new InvalidDataException("Expected a string value in the .voos save.");
    }

    internal static bool ReadBool(JsonNode? node, bool fallback = false)
    {
        if (node is null)
            return fallback;
        if (node is JsonValue value && value.TryGetValue<bool>(out bool result))
            return result;

        throw new InvalidDataException("Expected a boolean value in the .voos save.");
    }
}

public sealed class LegacyActorData
{
    private readonly JsonObject _data;

    internal LegacyActorData(JsonObject data) => _data = data;

    internal JsonObject Data => _data;

    public string Name => LegacySaveDocument.ReadString(_data["name"]);
    public string DisplayName => LegacySaveDocument.ReadString(_data["displayName"], Name);
    public string Description => LegacySaveDocument.ReadString(_data["description"]);
    public string RenderableUri => LegacySaveDocument.ReadString(_data["renderableUri"]);
    public string BrainName => LegacySaveDocument.ReadString(_data["brainName"]);
    public string MemoryJson => LegacySaveDocument.ReadString(_data["memoryJson"], "{}");
    public string TransformParent => LegacySaveDocument.ReadString(_data["transformParent"]);
    public bool IsOffstage => LegacySaveDocument.ReadBool(_data["preferOffstage"]);
    public bool EnablePhysics => LegacySaveDocument.ReadBool(_data["enablePhysics"]);
    public bool EnableGravity => LegacySaveDocument.ReadBool(_data["enableGravity"]);
    public bool IsSolid => LegacySaveDocument.ReadBool(_data["isSolid"], true);
    public bool IsPlayerControllable => LegacySaveDocument.ReadBool(_data["isPlayerControllable"]);
    public bool HideInPlayMode => LegacySaveDocument.ReadBool(_data["hideInPlayMode"]);
    public bool WasClonedByScript => LegacySaveDocument.ReadBool(_data["wasClonedByScript"]);
    public bool FreezeRotations => LegacySaveDocument.ReadBool(_data["freezeRotations"]);
    public bool FreezeX => LegacySaveDocument.ReadBool(_data["freezeX"]);
    public bool FreezeY => LegacySaveDocument.ReadBool(_data["freezeY"]);
    public bool FreezeZ => LegacySaveDocument.ReadBool(_data["freezeZ"]);
    public bool UseConcaveCollider => LegacySaveDocument.ReadBool(_data["useConcaveCollider"]);
    public float Mass => Math.Max(0.001f, LegacySaveDocument.ReadFloat(_data["mass"], 1f));
    public float Drag => Math.Max(0f, LegacySaveDocument.ReadFloat(_data["drag"], 0f));
    public float AngularDrag => Math.Max(0f, LegacySaveDocument.ReadFloat(_data["angularDrag"], 0.05f));
    public float Bounciness => Math.Clamp(LegacySaveDocument.ReadFloat(_data["bounciness"], 0f), 0f, 1f);
    public Color Tint => ReadColor(_data["tint"]);
    public Vector3 Velocity => CoordinateSpace.ToGodot(ReadVector(_data["velocity"], Vector3.Zero));
    public Vector3 AngularVelocity => CoordinateSpace.ToGodot(ReadVector(_data["angularVelocity"], Vector3.Zero));
    public Vector3 RenderableOffset => CoordinateSpace.ToGodot(ReadVector(_data["renderableOffset"], Vector3.Zero));
    public Quaternion RenderableRotation => CoordinateSpace.ToGodot(ReadQuaternion(_data["renderableRotation"]));

    public Transform3D GetWorldTransform()
    {
        Vector3 position = CoordinateSpace.ToGodot(ReadVector(_data["position"], Vector3.Zero));
        Vector3 scale = ReadVector(_data["localScale"], Vector3.One);
        Quaternion rotation = CoordinateSpace.ToGodot(ReadQuaternion(_data["rotation"]));
        return new Transform3D(new Basis(rotation).Scaled(scale), position);
    }

    public void SetWorldTransform(Transform3D transform)
    {
        JsonObject position = LegacySaveDocument.EnsureObject(_data, "position");
        WriteVector(position, CoordinateSpace.ToLegacy(transform.Origin));

        JsonObject rotation = LegacySaveDocument.EnsureObject(_data, "rotation");
        WriteQuaternion(rotation, CoordinateSpace.ToLegacy(transform.Basis.GetRotationQuaternion()));

        JsonObject scale = LegacySaveDocument.EnsureObject(_data, "localScale");
        WriteVector(scale, transform.Basis.Scale);
    }

    public void SetTransformParent(string parentName) => _data["transformParent"] = parentName;

    public void SetVelocity(Vector3 velocity)
    {
        JsonObject target = LegacySaveDocument.EnsureObject(_data, "velocity");
        WriteVector(target, CoordinateSpace.ToLegacy(velocity));
    }

    public void SetAngularVelocity(Vector3 angularVelocity)
    {
        JsonObject target = LegacySaveDocument.EnsureObject(_data, "angularVelocity");
        WriteVector(target, CoordinateSpace.ToLegacy(angularVelocity));
    }

    public Vector3 SpawnPosition => CoordinateSpace.ToGodot(ReadVector(
        _data["spawnPosition"],
        CoordinateSpace.ToLegacy(GetWorldTransform().Origin)));
    public Quaternion SpawnRotation => CoordinateSpace.ToGodot(ReadQuaternion(_data["spawnRotation"]));
    public string LoopingAnimation => LegacySaveDocument.ReadString(_data["loopingAnimation"]);

    public Vector3 ReadLegacyVector(string propertyName, Vector3 fallback)
        => ReadVector(_data[propertyName], fallback);

    public Quaternion ReadLegacyQuaternion(string propertyName)
        => ReadQuaternion(_data[propertyName]);

    public void WriteLegacyVector(string propertyName, Vector3 value)
        => WriteVector(LegacySaveDocument.EnsureObject(_data, propertyName), value);

    public void WriteLegacyQuaternion(string propertyName, Quaternion value)
        => WriteQuaternion(LegacySaveDocument.EnsureObject(_data, propertyName), value);

    public void WriteString(string propertyName, string value) => _data[propertyName] = value;
    public void WriteBool(string propertyName, bool value) => _data[propertyName] = value;
    public void WriteFloat(string propertyName, float value) => _data[propertyName] = value;
    public void WriteColor(Color value)
    {
        var color = LegacySaveDocument.EnsureObject(_data, "tint");
        color["r"] = value.R;
        color["g"] = value.G;
        color["b"] = value.B;
        color["a"] = value.A;
    }

    internal void SetPosition(Vector3 worldPosition)
    {
        JsonObject target = LegacySaveDocument.EnsureObject(_data, "position");
        WriteVector(target, CoordinateSpace.ToLegacy(worldPosition));
    }

    internal void SetRotation(Quaternion worldRotation)
    {
        JsonObject target = LegacySaveDocument.EnsureObject(_data, "rotation");
        WriteQuaternion(target, CoordinateSpace.ToLegacy(worldRotation));
    }

    internal void SetScale(Vector3 scale)
    {
        JsonObject target = LegacySaveDocument.EnsureObject(_data, "localScale");
        WriteVector(target, scale);
    }

    internal static Vector3 ReadVector(JsonNode? node, Vector3 fallback)
    {
        if (node is not JsonObject vector)
            return fallback;
        return new Vector3(
            LegacySaveDocument.ReadFloat(vector["x"], fallback.X),
            LegacySaveDocument.ReadFloat(vector["y"], fallback.Y),
            LegacySaveDocument.ReadFloat(vector["z"], fallback.Z));
    }

    internal static Quaternion ReadQuaternion(JsonNode? node)
    {
        if (node is not JsonObject quaternion)
            return Quaternion.Identity;
        var value = new Quaternion(
            LegacySaveDocument.ReadFloat(quaternion["x"], 0f),
            LegacySaveDocument.ReadFloat(quaternion["y"], 0f),
            LegacySaveDocument.ReadFloat(quaternion["z"], 0f),
            LegacySaveDocument.ReadFloat(quaternion["w"], 1f));
        return value.LengthSquared() > 0f ? value.Normalized() : Quaternion.Identity;
    }

    private static Color ReadColor(JsonNode? node)
    {
        if (node is not JsonObject color)
            return Colors.White;
        return new Color(
            LegacySaveDocument.ReadFloat(color["r"], 1f),
            LegacySaveDocument.ReadFloat(color["g"], 1f),
            LegacySaveDocument.ReadFloat(color["b"], 1f),
            LegacySaveDocument.ReadFloat(color["a"], 1f));
    }

    private static void WriteVector(JsonObject target, Vector3 value)
    {
        target["x"] = value.X;
        target["y"] = value.Y;
        target["z"] = value.Z;
    }

    private static void WriteQuaternion(JsonObject target, Quaternion value)
    {
        target["x"] = value.X;
        target["y"] = value.Y;
        target["z"] = value.Z;
        target["w"] = value.W;
    }
}

public static class CoordinateSpace
{
    public static Vector3 ToGodot(Vector3 unity) => new(unity.X, unity.Y, -unity.Z);
    public static Vector3 ToLegacy(Vector3 godot) => new(godot.X, godot.Y, -godot.Z);

    public static Quaternion ToGodot(Quaternion unity) => new(-unity.X, -unity.Y, unity.Z, unity.W);
    public static Quaternion ToLegacy(Quaternion godot) => new(-godot.X, -godot.Y, godot.Z, godot.W);
}
