// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Db.FullPruning;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain.FullPruning;

public class PruningTriggerPruningStrategy : IPruningStrategy, IDisposable
{
    // While full pruning runs, force a snapshot once this many persistable blocks have accumulated
    // behind the pruning boundary, so the best persisted state keeps advancing.
    private const ulong SnapshotIntervalDuringFullPruning = 32;

    private readonly IFullPruningDb _fullPruningDb;
    private readonly IPruningStrategy _basePruningStrategy;
    private readonly IPruningStrategy _duringFullPruningStrategy;
    private IPruningContext? _pruningContext;

    public PruningTriggerPruningStrategy(
        IFullPruningDb fullPruningDb,
        IPruningStrategy basePruningStrategy,
        ulong pruningBoundary)
    {
        _fullPruningDb = fullPruningDb;
        _basePruningStrategy = basePruningStrategy;
        // Full pruning needs the best persisted state to keep changing, so while it runs the base strategy is
        // wrapped in the same "last persisted block is too old" trigger the dirty-cache path already uses. Only
        // blocks older than the pruning boundary can be persisted, and that helper measures from the boundary
        // rather than from the head - a head-relative comparison is always true during a full prune, because the
        // head is by construction at least `pruningBoundary` ahead of the last persisted block, so it forces a
        // snapshot on every single block.
        _duringFullPruningStrategy = basePruningStrategy.WhenLastPersistedBlockIsTooOld(
            SnapshotIntervalDuringFullPruning, pruningBoundary);
        _fullPruningDb.PruningFinished += OnPruningFinished;
        _fullPruningDb.PruningStarted += OnPruningStarted;
    }

    private void OnPruningStarted(object? sender, PruningEventArgs e) => Interlocked.Exchange(ref _pruningContext, e.Context);

    private void OnPruningFinished(object? sender, PruningEventArgs e) => Interlocked.CompareExchange(ref _pruningContext, null, e.Context);

    public bool DeleteObsoleteKeys
    {
        get
        {
            bool inPruning = Volatile.Read(ref _pruningContext) is not null;
            return !inPruning && _basePruningStrategy.DeleteObsoleteKeys;
        }
    }

    public bool ShouldPruneDirtyNode(TrieStoreState state) =>
        (Volatile.Read(ref _pruningContext) is not null ? _duringFullPruningStrategy : _basePruningStrategy).ShouldPruneDirtyNode(state);

    public bool ShouldPrunePersistedNode(TrieStoreState state) => _basePruningStrategy.ShouldPrunePersistedNode(state);

    /// <inheritdoc/>
    public void Dispose()
    {
        _fullPruningDb.PruningStarted -= OnPruningStarted;
        _fullPruningDb.PruningFinished -= OnPruningFinished;
    }
}
