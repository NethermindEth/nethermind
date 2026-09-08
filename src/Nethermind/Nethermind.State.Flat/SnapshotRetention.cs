// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

/// <summary>Coordinates snapshot pruning with the lifetime of assembled state views.</summary>
/// <remarks>
/// Budget-driven orphan pruning retains registered heads and their ancestry. Persistence may still
/// remove repository entries as the durable boundary advances; assembled views keep independent
/// snapshot leases, so removing those entries does not invalidate their reads.
/// </remarks>
public sealed class SnapshotRetention
{
    private readonly Dictionary<StateId, int> _activeHeads = [];

    internal Lock Sync { get; } = new();

    /// <summary>Heads whose ancestry must remain available; the caller must hold <see cref="Sync"/>.</summary>
    internal Dictionary<StateId, int>.KeyCollection ActiveHeads => _activeHeads.Keys;

    internal void Register(in StateId head)
    {
        using Lock.Scope scope = Sync.EnterScope();
        _activeHeads.TryGetValue(head, out int count);
        _activeHeads[head] = count + 1;
    }

    internal void Release(in StateId head)
    {
        using Lock.Scope scope = Sync.EnterScope();
        int count = _activeHeads[head];
        if (count == 1) _activeHeads.Remove(head);
        else _activeHeads[head] = count - 1;
    }
}
