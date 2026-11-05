using GBNG.World;

namespace GBNG.Behaviors;

internal sealed class LegacyPlayerControlSlots
{
    private readonly List<string?> _actorNamesBySlot = new();
    private readonly Dictionary<string, int> _slotsByActorName = new(StringComparer.Ordinal);
    private readonly HashSet<string> _currentPlayerActorNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observedPlayerActorNames = new(StringComparer.Ordinal);
    private readonly List<string> _playerActorNamesToForget = new();

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
    }

    private void ClearSlot(int slotIndex)
    {
        string? actorName = _actorNamesBySlot[slotIndex];
        if (actorName is null)
            return;

        _actorNamesBySlot[slotIndex] = null;
        _slotsByActorName.Remove(actorName);
    }
}
