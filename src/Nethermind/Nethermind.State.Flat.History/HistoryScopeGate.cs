// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Lets the window pruner wait for read scopes admitted under an old floor before deleting the rows they may still
/// be reading. Each scope records its floor generation; a drain waits only for scopes below the current one.
/// </summary>
/// <remarks><see cref="TryDrainForFloorAdvance"/> must be called AFTER publishing the new floor.</remarks>
public sealed class HistoryScopeGate
{
    private readonly ConcurrentDictionary<long, long> _activeScopes = new();
    private long _nextScopeId;
    private long _floorGeneration;

    /// <summary>Returns the token for the matching <see cref="ExitScope"/>. A registration that races a drain may
    /// be missed by its sweep, which is safe: the caller validates the floor after this returns.</summary>
    public long EnterScope()
    {
        long scopeId = Interlocked.Increment(ref _nextScopeId);
        _activeScopes[scopeId] = Volatile.Read(ref _floorGeneration);
        return scopeId;
    }

    public void ExitScope(long scopeId) => _activeScopes.TryRemove(scopeId, out _);

    /// <summary>Bumps the floor generation. A retried drain must re-wait on the generation it got here rather than
    /// bump again, which would also demote scopes already safe by their own admission check.</summary>
    internal long BeginFloorAdvance() => Interlocked.Increment(ref _floorGeneration);

    /// <summary>Bumps and drains in one step - the shape every first attempt wants.</summary>
    internal bool TryDrainForFloorAdvance(TimeSpan timeout, CancellationToken token) =>
        TryDrain(BeginFloorAdvance(), timeout, token);

    /// <summary>Waits, bounded, for every scope older than <paramref name="generation"/> to close.</summary>
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
