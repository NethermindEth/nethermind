// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Lets the window pruner wait for historical read scopes admitted under an old floor to finish before it deletes
/// the rows they may still be reading. Each scope records the floor generation it was admitted under; a drain
/// bumps the generation and waits only for scopes below it, so scopes admitted after the floor publish (safe by
/// their own admission check) never delay a drain and a single stuck scope cannot make later drains wait on
/// unrelated readers.
/// </summary>
/// <remarks>
/// The pruner must call <see cref="TryDrainForFloorAdvance"/> AFTER publishing the new floor: a scope opened
/// after publish re-reads the floor at its own admission check, so it is safe by construction and carries the
/// bumped generation.
/// </remarks>
public sealed class HistoryScopeGate
{
    private readonly ConcurrentDictionary<long, long> _activeScopes = new();
    private long _nextScopeId;
    private long _floorGeneration;

    /// <summary>Marks a historical read scope as open, returning the token for the matching
    /// <see cref="ExitScope"/> call. A registration that races a drain may be missed by its sweep, which is safe:
    /// the caller validates availability against the already-published floor only after this returns, so a scope
    /// the sweep did not wait for fails closed instead of reading rows the pruner is deleting.</summary>
    public long EnterScope()
    {
        long scopeId = Interlocked.Increment(ref _nextScopeId);
        _activeScopes[scopeId] = Volatile.Read(ref _floorGeneration);
        return scopeId;
    }

    public void ExitScope(long scopeId) => _activeScopes.TryRemove(scopeId, out _);

    /// <summary>Bumps the floor generation, returning the value every scope open at this moment counts as older
    /// than. A caller that has to retry its drain must re-wait on the generation it got here rather than bump
    /// again: a fresh bump would also demote scopes admitted since the floor was published, and those are safe by
    /// their own admission check - waiting for them would stall the pruner behind readers it has no reason to.
    /// </summary>
    internal long BeginFloorAdvance() => Interlocked.Increment(ref _floorGeneration);

    /// <summary>Bumps and drains in one step - the shape every first attempt wants.</summary>
    internal bool TryDrainForFloorAdvance(TimeSpan timeout, CancellationToken token) =>
        TryDrain(BeginFloorAdvance(), timeout, token);

    /// <summary>Waits (bounded) for every scope admitted under a generation older than <paramref name="generation"/>
    /// to close. On timeout no state needs restoring - the caller keeps the generation and waits again.</summary>
    internal bool TryDrain(long generation, TimeSpan timeout, CancellationToken token)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (AnyScopeBelow(generation))
        {
            if (token.IsCancellationRequested || stopwatch.Elapsed >= timeout) return false;

            Thread.Sleep(10);
        }

        return true;
    }

    private bool AnyScopeBelow(long generation)
    {
        foreach (KeyValuePair<long, long> scope in _activeScopes)
        {
            if (scope.Value < generation) return true;
        }

        return false;
    }
}
