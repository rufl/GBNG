using System.Globalization;
using System.Text.Json;
using GBNG.Domain;
using GBNG.Networking;
using GBNG.World;

namespace GBNG.Behaviors;

internal sealed class LegacyPlayerControlSlots
{
    private readonly List<string?> _actorNamesBySlot = new();
    private readonly Dictionary<string, int> _slotsByActorName = new(StringComparer.Ordinal);
    private readonly Dictionary<long, int> _slotsByPlayerId = new();
    private readonly Dictionary<int, string> _actorNamesByCurrentSlot = new();
    private readonly HashSet<int> _ambiguousSlots = new();
    private readonly Dictionary<int, string?> _slotOverridesBySlot = new();
    private bool _hasAuthoritativeNetworkSlots;
    private string? _serializedSlotsJson;

    internal LegacyPlayerControlSlots(
        IReadOnlyList<WorldActorNode> actors,
        IReadOnlyCollection<LegacyLanPlayer> players,
        bool hasPeer,
        LegacySaveDocument? document = null)
    {
        Reconcile(actors, players, hasPeer, document);
    }

    internal void Reconcile(
        IReadOnlyList<WorldActorNode> actors,
        IReadOnlyCollection<LegacyLanPlayer> players,
        bool hasPeer,
        LegacySaveDocument? document = null)
    {
        _slotsByPlayerId.Clear();
        if (hasPeer)
        {
            foreach (LegacyLanPlayer player in players)
            {
                if (player.SlotNumber > 0)
                    _slotsByPlayerId.TryAdd(player.Id, player.SlotNumber);
            }
        }

        _actorNamesByCurrentSlot.Clear();
        _ambiguousSlots.Clear();
        bool hasControllerIdentity = false;
        foreach (WorldActorNode actor in actors)
        {
            string playerId = LegacySaveDocument.ReadString(actor.State.Data["controllingVirtualPlayerId"]);
            hasControllerIdentity |= hasPeer
                ? playerId.Length > 0 && playerId != "local"
                : playerId == "local";
            int playerSlot;
            if (!hasPeer)
            {
                if (playerId != "local")
                    continue;
                playerSlot = 1;
            }
            else if (!long.TryParse(playerId, NumberStyles.None, CultureInfo.InvariantCulture, out long numericPlayerId)
                || !_slotsByPlayerId.TryGetValue(numericPlayerId, out playerSlot))
            {
                continue;
            }

            if (_ambiguousSlots.Contains(playerSlot))
                continue;
            if (_actorNamesByCurrentSlot.TryGetValue(playerSlot, out string? existingActorName))
            {
                if (existingActorName != actor.LegacyName)
                {
                    _actorNamesByCurrentSlot.Remove(playerSlot);
                    _ambiguousSlots.Add(playerSlot);
                }
            }
            else
            {
                _actorNamesByCurrentSlot.Add(playerSlot, actor.LegacyName);
            }
        }

        if (!hasControllerIdentity)
        {
            if (hasPeer)
            {
                foreach (LegacyLanPlayer player in players)
                {
                    WorldActorNode? actor = WorldActorNode.FindPlayerActorAtSlot(
                        actors, player.SlotNumber, players, hasPeer: true, document);
                    if (actor is not null)
                        _actorNamesByCurrentSlot.TryAdd(player.SlotNumber, actor.LegacyName);
                }
            }
            else if (WorldActorNode.FindPlayerActorAtSlot(
                actors, 1, players, hasPeer: false, document) is WorldActorNode localPlayer)
            {
                _actorNamesByCurrentSlot.Add(1, localPlayer.LegacyName);
            }
        }
        if (_hasAuthoritativeNetworkSlots)
        {
            _actorNamesByCurrentSlot.Clear();
            foreach (KeyValuePair<int, string?> assignment in _slotOverridesBySlot)
            {
                if (assignment.Value is not null)
                    _actorNamesByCurrentSlot[assignment.Key] = assignment.Value;
            }
        }
        else
        {
            foreach (KeyValuePair<int, string?> assignment in _slotOverridesBySlot)
            {
                if (assignment.Value is null)
                    _actorNamesByCurrentSlot.Remove(assignment.Key);
                else
                    _actorNamesByCurrentSlot[assignment.Key] = assignment.Value;
            }
        }

        for (int slotIndex = _actorNamesBySlot.Count - 1; slotIndex >= 0; slotIndex--)
        {
            int playerSlot = slotIndex + 1;
            if (!_actorNamesByCurrentSlot.ContainsKey(playerSlot))
                SetSlot(playerSlot, null);
        }
        foreach (KeyValuePair<int, string> assignment in _actorNamesByCurrentSlot)
            SetSlot(assignment.Key, assignment.Value);
        TrimTrailingEmptySlots();
    }

    internal bool TryApplyNetworkSlots(IReadOnlyList<WorldActorNode> actors, string json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 16 * 1024 * 1024)
            return false;

        string?[]? actorNames;
        try
        {
            actorNames = JsonSerializer.Deserialize<string?[]>(json);
        }
        catch (JsonException)
        {
            return false;
        }
        if (actorNames is null || actorNames.Length > ushort.MaxValue)
            return false;

        var actorsByName = new Dictionary<string, WorldActorNode>(actors.Count, StringComparer.Ordinal);
        foreach (WorldActorNode actor in actors)
        {
            if (!actorsByName.TryAdd(actor.LegacyName, actor))
                return false;
        }
        var assignedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? actorName in actorNames)
        {
            if (actorName is not null
                && (string.IsNullOrWhiteSpace(actorName)
                    || !assignedNames.Add(actorName)
                    || !actorsByName.ContainsKey(actorName)))
                return false;
        }

        _hasAuthoritativeNetworkSlots = true;
        _slotOverridesBySlot.Clear();
        for (int index = 0; index < actorNames.Length; index++)
            _slotOverridesBySlot[index + 1] = actorNames[index];
        _actorNamesBySlot.Clear();
        _slotsByActorName.Clear();
        foreach (string? actorName in actorNames)
        {
            _actorNamesBySlot.Add(actorName);
            if (actorName is null)
                continue;

            WorldActorNode actor = actorsByName[actorName];
            bool needsPhysicsUpdate = !actor.State.IsPlayerControllable;
            actor.State.WriteBool("isPlayerControllable", true);
            if (needsPhysicsUpdate)
                actor.ApplyLegacyPhysicsSettings();
            _slotsByActorName.Add(actorName, _actorNamesBySlot.Count);
        }
        TrimTrailingEmptySlots();
        _serializedSlotsJson = JsonSerializer.Serialize(_actorNamesBySlot);
        return true;
    }


    internal bool TryTransferControl(
        int playerSlot,
        string fromActorName,
        WorldActorNode? target,
        string playerId,
        IReadOnlyList<WorldActorNode> actors)
    {
        if (playerSlot <= 0 || GetSlotForActor(fromActorName) != playerSlot || string.IsNullOrEmpty(playerId))
            return false;
        if (target is not null && target.LegacyName == fromActorName)
            return false;
        int previousTargetSlot = target is null ? 0 : GetSlotForActor(target.LegacyName);

        foreach (WorldActorNode actor in actors)
        {
            string controllingPlayerId = LegacySaveDocument.ReadString(actor.State.Data["controllingVirtualPlayerId"]);
            if (actor.LegacyName == fromActorName
                || actor == target
                || controllingPlayerId == playerId)
                actor.State.WriteString("controllingVirtualPlayerId", string.Empty);
        }

        if (target is not null)
        {
            target.State.WriteBool("isPlayerControllable", true);
            target.State.WriteString("controllingVirtualPlayerId", playerId);
        }
        _slotOverridesBySlot[playerSlot] = target?.LegacyName;
        if (previousTargetSlot > 0 && previousTargetSlot != playerSlot)
            _slotOverridesBySlot[previousTargetSlot] = null;
        return true;
    }
    internal void ClearNetworkOverrides()
    {
        if (!_hasAuthoritativeNetworkSlots)
            return;
        _hasAuthoritativeNetworkSlots = false;
        _slotOverridesBySlot.Clear();
        _actorNamesBySlot.Clear();
        _slotsByActorName.Clear();
        _serializedSlotsJson = null;
    }

    internal WorldActorNode? GetActorAtSlot(IReadOnlyList<WorldActorNode> actors, int playerSlot)
    {
        if (playerSlot <= 0 || playerSlot > _actorNamesBySlot.Count)
            return null;

        string? actorName = _actorNamesBySlot[playerSlot - 1];
        if (actorName is null)
            return null;

        foreach (WorldActorNode actor in actors)
        {
            if (actor.LegacyName == actorName)
                return actor;
        }
        return null;
    }

    internal int GetSlotForActor(string actorName)
        => _slotsByActorName.TryGetValue(actorName, out int playerSlot) ? playerSlot : 0;

    internal string ToJson()
        => _serializedSlotsJson ??= JsonSerializer.Serialize(_actorNamesBySlot);


    private void SetSlot(int playerSlot, string? actorName)
    {
        while (_actorNamesBySlot.Count < playerSlot)
            _actorNamesBySlot.Add(null);

        int slotIndex = playerSlot - 1;
        string? previousActorName = _actorNamesBySlot[slotIndex];
        if (previousActorName == actorName)
            return;
        if (previousActorName is not null)
            _slotsByActorName.Remove(previousActorName);

        if (actorName is not null && _slotsByActorName.TryGetValue(actorName, out int previousSlot))
        {
            if (previousSlot != playerSlot)
                _actorNamesBySlot[previousSlot - 1] = null;
        }

        _actorNamesBySlot[slotIndex] = actorName;
        if (actorName is not null)
            _slotsByActorName[actorName] = playerSlot;
        _serializedSlotsJson = null;
        TrimTrailingEmptySlots();
    }


    private void TrimTrailingEmptySlots()
    {
        while (_actorNamesBySlot.Count > 0 && _actorNamesBySlot[^1] is null)
        {
            _actorNamesBySlot.RemoveAt(_actorNamesBySlot.Count - 1);
            _serializedSlotsJson = null;
        }
    }
}
