using System.Text.Json;
using GBNG.World;

namespace GBNG.Behaviors;

internal sealed class LegacyPlayerControlSlots
{
    private readonly List<string?> _actorNamesBySlot = new();
    private readonly Dictionary<string, int> _slotsByActorName = new(StringComparer.Ordinal);
    private readonly HashSet<string> _currentPlayerActorNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observedPlayerActorNames = new(StringComparer.Ordinal);
    private readonly List<string> _playerActorNamesToForget = new();
    private string? _serializedSlotsJson;

    internal LegacyPlayerControlSlots(IReadOnlyList<WorldActorNode> actors)
    {
        Reconcile(actors);
    }

    internal void Reconcile(IReadOnlyList<WorldActorNode> actors)
    {
        _currentPlayerActorNames.Clear();
        foreach (WorldActorNode actor in actors)
        {
            if (actor.State.IsPlayerControllable)
                _currentPlayerActorNames.Add(actor.LegacyName);
        }

        for (int index = 0; index < _actorNamesBySlot.Count; index++)
        {
            string? actorName = _actorNamesBySlot[index];
            if (actorName is not null && !_currentPlayerActorNames.Contains(actorName))
                ClearSlot(index);
        }

        _playerActorNamesToForget.Clear();
        foreach (string actorName in _observedPlayerActorNames)
        {
            if (!_currentPlayerActorNames.Contains(actorName))
                _playerActorNamesToForget.Add(actorName);
        }
        foreach (string actorName in _playerActorNamesToForget)
            _observedPlayerActorNames.Remove(actorName);

        foreach (WorldActorNode actor in actors)
        {
            if (actor.State.IsPlayerControllable && _observedPlayerActorNames.Add(actor.LegacyName))
                AssignFirstAvailableSlot(actor.LegacyName);
        }
        TrimTrailingEmptySlots();
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

        _actorNamesBySlot.Clear();
        _slotsByActorName.Clear();
        _observedPlayerActorNames.Clear();
        _currentPlayerActorNames.Clear();
        _playerActorNamesToForget.Clear();
        foreach (WorldActorNode actor in actors)
        {
            if (actor.State.IsPlayerControllable)
                _observedPlayerActorNames.Add(actor.LegacyName);
        }
        for (int index = 0; index < actorNames.Length; index++)
        {
            string? actorName = actorNames[index];
            _actorNamesBySlot.Add(actorName);
            if (actorName is null)
                continue;

            WorldActorNode actor = actorsByName[actorName];
            bool needsPhysicsUpdate = !actor.State.IsPlayerControllable;
            actor.State.WriteBool("isPlayerControllable", true);
            if (needsPhysicsUpdate)
                actor.ApplyLegacyPhysicsSettings();
            _observedPlayerActorNames.Add(actorName);
            _slotsByActorName.Add(actorName, index + 1);
        }
        TrimTrailingEmptySlots();
        _serializedSlotsJson = JsonSerializer.Serialize(_actorNamesBySlot);
        return true;
    }


    internal bool TryTransferControl(int playerSlot, string fromActorName, WorldActorNode? target)
    {
        if (playerSlot <= 0 || GetSlotForActor(fromActorName) != playerSlot)
            return false;
        if (target is not null && target.LegacyName == fromActorName)
            return false;

        if (target is not null)
        {
            target.State.WriteBool("isPlayerControllable", true);
            _observedPlayerActorNames.Add(target.LegacyName);
        }

        SetSlot(playerSlot, target?.LegacyName);
        return true;
    }

    private void AssignFirstAvailableSlot(string actorName)
    {
        int emptyIndex = _actorNamesBySlot.IndexOf(null);
        int playerSlot = emptyIndex >= 0 ? emptyIndex + 1 : _actorNamesBySlot.Count + 1;
        SetSlot(playerSlot, actorName);
    }

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

    private void ClearSlot(int slotIndex)
    {
        string? actorName = _actorNamesBySlot[slotIndex];
        if (actorName is null)
            return;

        _actorNamesBySlot[slotIndex] = null;
        _slotsByActorName.Remove(actorName);
        _serializedSlotsJson = null;
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
