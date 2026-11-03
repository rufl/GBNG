using System.Text.Json.Nodes;
using GBNG.Domain;

namespace GBNG.Networking;

internal static class LegacyDocumentPatch
{
    internal const int MaximumOperations = 8_192;

    public static JsonArray Create(JsonObject before, JsonObject after)
    {
        var operations = new JsonArray();
        DiffObjects(before, after, [], operations, actorName: null);
        if (operations.Count > MaximumOperations)
            throw new InvalidDataException($"A collaborative edit exceeds the {MaximumOperations:N0}-operation limit.");
        return operations;
    }

    public static JsonArray CreateSnapshot(JsonObject document)
    {
        var operations = new JsonArray();
        foreach (string property in new[] { "stage", "gridState", "behaviorDatabase", "sojoDatabase" })
        {
            var path = new JsonArray(property);
            if (document[property] is JsonObject value)
            {
                operations.Add(new JsonObject
                {
                    ["scope"] = "document",
                    ["op"] = "set",
                    ["path"] = path,
                    ["value"] = value.DeepClone()
                });
            }
            else
            {
                operations.Add(new JsonObject
                {
                    ["scope"] = "document",
                    ["op"] = "remove",
                    ["path"] = path
                });
            }
        }
        return operations;
    }

    public static bool TryParse(string json, out JsonArray operations, out string error)
    {
        operations = new JsonArray();
        error = string.Empty;
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json, documentOptions: new System.Text.Json.JsonDocumentOptions { MaxDepth = 48 });
        }
        catch (System.Text.Json.JsonException exception)
        {
            error = $"Invalid collaborative edit: {exception.Message}";
            return false;
        }

        if (parsed is not JsonArray array || array.Count is 0 or > MaximumOperations)
        {
            error = $"Collaborative edits must contain between 1 and {MaximumOperations:N0} operations.";
            return false;
        }

        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject operation || !IsValidOperation(operation))
            {
                error = "The collaborative edit contains an unsupported operation.";
                return false;
            }
            operations.Add(operation.DeepClone());
        }
        return true;
    }

    public static void ApplyDocumentOperations(
        JsonObject root,
        JsonArray operations,
        HashSet<string> changedRootProperties)
    {
        foreach (JsonObject operation in operations.OfType<JsonObject>())
        {
            if (ReadString(operation, "scope") != "document")
                continue;
            JsonArray path = (JsonArray)operation["path"]!;
            string rootProperty = ReadString(path[0]);
            ApplyPath(root, path, operation);
            changedRootProperties.Add(rootProperty);
        }
    }

    public static void ApplyActorOperations(JsonArray actors, JsonArray operations)
    {
        foreach (JsonObject operation in operations.OfType<JsonObject>())
        {
            if (ReadString(operation, "scope") != "actor")
                continue;

            string actorName = ReadString(operation, "actor");
            string kind = ReadString(operation, "op");
            int index = FindActorIndex(actors, actorName);
            if (kind == "add")
            {
                JsonObject actor = (JsonObject)operation["value"]!.DeepClone();
                if (index >= 0)
                    actors[index] = actor;
                else
                    actors.Add(actor);
                continue;
            }
            if (kind == "delete")
            {
                if (index >= 0)
                    actors.RemoveAt(index);
                continue;
            }
            if (index < 0 || actors[index] is not JsonObject actorData)
                continue;
            ApplyPath(actorData, (JsonArray)operation["path"]!, operation);
        }

        if (actors.Count > ushort.MaxValue)
            throw new InvalidDataException("The collaborative edit exceeds the actor-count limit.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in actors)
        {
            if (node is not JsonObject actor || !TryReadActorName(actor, out string name) || !names.Add(name))
                throw new InvalidDataException("The collaborative edit contains an invalid or duplicate actor.");
        }
    }

    private static void DiffObjects(
        JsonObject before,
        JsonObject after,
        List<string> path,
        JsonArray operations,
        string? actorName)
    {
        var keys = new HashSet<string>(before.Select(pair => pair.Key), StringComparer.Ordinal);
        keys.UnionWith(after.Select(pair => pair.Key));
        foreach (string key in keys.Order(StringComparer.Ordinal))
        {
            bool hadBefore = before.TryGetPropertyValue(key, out JsonNode? beforeValue);
            bool hasAfter = after.TryGetPropertyValue(key, out JsonNode? afterValue);
            path.Add(key);
            if (actorName is null && path.Count == 2 && path[0] == "voosEngineState" && key == "actors")
                DiffActors(beforeValue, afterValue, operations);
            else
                DiffValue(hadBefore, beforeValue, hasAfter, afterValue, path, operations, actorName);
            path.RemoveAt(path.Count - 1);
        }
    }

    private static void DiffActors(JsonNode? beforeNode, JsonNode? afterNode, JsonArray operations)
    {
        if (beforeNode is not JsonArray before || afterNode is not JsonArray after)
            throw new InvalidDataException("The collaborative actor list is invalid.");

        Dictionary<string, JsonObject> oldActors = MapActors(before);
        Dictionary<string, JsonObject> newActors = MapActors(after);
        foreach (string name in oldActors.Keys.Union(newActors.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            bool existedBefore = oldActors.TryGetValue(name, out JsonObject? beforeActor);
            bool existsAfter = newActors.TryGetValue(name, out JsonObject? afterActor);
            if (!existsAfter)
            {
                operations.Add(new JsonObject { ["scope"] = "actor", ["op"] = "delete", ["actor"] = name });
            }
            else if (!existedBefore)
            {
                operations.Add(new JsonObject
                {
                    ["scope"] = "actor",
                    ["op"] = "add",
                    ["actor"] = name,
                    ["value"] = afterActor!.DeepClone()
                });
            }
            else
            {
                DiffObjects(beforeActor!, afterActor!, [], operations, name);
            }
        }
    }

    private static Dictionary<string, JsonObject> MapActors(JsonArray actors)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (JsonNode? node in actors)
        {
            if (node is not JsonObject actor || !TryReadActorName(actor, out string name) || !result.TryAdd(name, actor))
                throw new InvalidDataException("The collaborative actor list contains an invalid or duplicate actor.");
        }
        return result;
    }

    private static void DiffValue(
        bool hadBefore,
        JsonNode? before,
        bool hasAfter,
        JsonNode? after,
        List<string> path,
        JsonArray operations,
        string? actorName)
    {
        if (!hasAfter)
        {
            AddOperation(operations, actorName, "remove", path, null, hasValue: false);
            return;
        }
        if (!hadBefore)
        {
            AddOperation(operations, actorName, "set", path, after, hasValue: true);
            return;
        }
        if (JsonNode.DeepEquals(before, after))
            return;
        if (before is JsonObject beforeObject && after is JsonObject afterObject)
        {
            DiffObjects(beforeObject, afterObject, path, operations, actorName);
            return;
        }
        AddOperation(operations, actorName, "set", path, after, hasValue: true);
    }

    private static void AddOperation(
        JsonArray operations,
        string? actorName,
        string kind,
        IReadOnlyList<string> path,
        JsonNode? value,
        bool hasValue)
    {
        var pathNode = new JsonArray();
        foreach (string segment in path)
            pathNode.Add(segment);
        var operation = new JsonObject
        {
            ["scope"] = actorName is null ? "document" : "actor",
            ["op"] = kind,
            ["path"] = pathNode
        };
        if (actorName is not null)
            operation["actor"] = actorName;
        if (hasValue)
            operation["value"] = value?.DeepClone();
        operations.Add(operation);
    }

    private static void ApplyPath(JsonObject root, JsonArray path, JsonObject operation)
    {
        var parent = root;
        for (int index = 0; index < path.Count - 1; index++)
        {
            string segment = ReadString(path[index]);
            if (parent[segment] is JsonObject child)
            {
                parent = child;
                continue;
            }
            var replacement = new JsonObject();
            parent[segment] = replacement;
            parent = replacement;
        }

        string property = ReadString(path[^1]);
        if (ReadString(operation, "op") == "remove")
            parent.Remove(property);
        else
            parent[property] = operation["value"]?.DeepClone();
    }

    private static int FindActorIndex(JsonArray actors, string actorName)
    {
        for (int index = 0; index < actors.Count; index++)
        {
            if (actors[index] is JsonObject actor && TryReadActorName(actor, out string name) && name == actorName)
                return index;
        }
        return -1;
    }

    private static bool IsValidOperation(JsonObject operation)
    {
        string scope = ReadString(operation, "scope");
        string kind = ReadString(operation, "op");
        if (scope == "document")
        {
            if (kind is not ("set" or "remove") || operation["path"] is not JsonArray path || path.Count is < 1 or > 32)
                return false;
            string rootProperty = ReadString(path[0]);
            if (rootProperty is not ("stage" or "gridState" or "behaviorDatabase" or "sojoDatabase"))
                return false;
            if (kind == "set" && !operation.ContainsKey("value"))
                return false;
            if (path.Count == 1 && kind == "set" && operation["value"] is not JsonObject)
                return false;
            return path.All(segment => segment is JsonValue value && value.TryGetValue(out string? text) && text is { Length: > 0 and <= 512 });
        }

        string actorName = ReadString(operation, "actor");
        if (scope != "actor" || actorName.Length is 0 or > 256)
            return false;
        if (kind == "add")
            return operation["value"] is JsonObject actor && TryReadActorName(actor, out string name) && name == actorName;
        if (kind == "delete")
            return true;
        if (kind is not ("set" or "remove") || operation["path"] is not JsonArray actorPath || actorPath.Count is < 1 or > 32)
            return false;
        if (kind == "set" && !operation.ContainsKey("value"))
            return false;
        if (ReadString(actorPath[0]) == "name")
            return false;
        return actorPath.All(segment => segment is JsonValue value && value.TryGetValue(out string? text) && text is { Length: > 0 and <= 512 });
    }

    private static bool TryReadActorName(JsonObject actor, out string name)
    {
        name = ReadString(actor, "name");
        return name.Length is > 0 and <= 256;
    }

    private static string ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue(out string? text) ? text ?? string.Empty : string.Empty;

    private static string ReadString(JsonObject parent, string name)
        => ReadString(parent[name]);
}
