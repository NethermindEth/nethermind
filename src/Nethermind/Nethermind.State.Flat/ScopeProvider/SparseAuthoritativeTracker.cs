// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Tracks per-provider statistics for sparse-vs-Patricia state-root agreement so a
/// <see cref="FlatScopeProvider"/> can decide when to skip Patricia work and let sparse drive
/// commit/root computation. Lives on the provider (not on the static state of
/// <see cref="FlatWorldStateScope"/>) so read-only scopes (RPC) and parallel processing
/// pipelines do not poison each other's confidence counter.
/// </summary>
public sealed class SparseAuthoritativeTracker
{
    private int _matchCount;
    private int _mismatchCount;
    private int _failCount;
    private int _consecutiveMatches;

    public int MatchCount => Volatile.Read(ref _matchCount);
    public int MismatchCount => Volatile.Read(ref _mismatchCount);
    public int FailCount => Volatile.Read(ref _failCount);
    public int ConsecutiveMatches => Volatile.Read(ref _consecutiveMatches);

    /// <returns>The new total match count after recording this match.</returns>
    public int RecordMatch()
    {
        Interlocked.Increment(ref _consecutiveMatches);
        return Interlocked.Increment(ref _matchCount);
    }

    /// <returns>The new total mismatch count after recording this mismatch.</returns>
    public int RecordMismatch()
    {
        Interlocked.Exchange(ref _consecutiveMatches, 0);
        return Interlocked.Increment(ref _mismatchCount);
    }

    /// <returns>The new total fail count after recording this fail.</returns>
    public int RecordFail()
    {
        Interlocked.Exchange(ref _consecutiveMatches, 0);
        return Interlocked.Increment(ref _failCount);
    }

    public void ResetConsecutive() => Interlocked.Exchange(ref _consecutiveMatches, 0);
}
