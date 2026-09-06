// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Extensions;
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
    private readonly ulong _pruningBoundary;
    private int _inPruning = 0;

    public PruningTriggerPruningStrategy(
        IFullPruningDb fullPruningDb,
        IPruningStrategy basePruningStrategy,
        ulong pruningBoundary)
    {
        _fullPruningDb = fullPruningDb;
        _basePruningStrategy = basePruningStrategy;
        _pruningBoundary = pruningBoundary;
        _fullPruningDb.PruningFinished += OnPruningFinished;
        _fullPruningDb.PruningStarted += OnPruningStarted;
    }

    private void OnPruningStarted(object? sender, EventArgs e) => Interlocked.CompareExchange(ref _inPruning, 1, 0);

    private void OnPruningFinished(object? sender, EventArgs e) => Interlocked.CompareExchange(ref _inPruning, 0, 1);

    public bool DeleteObsoleteKeys
    {
        get
        {
            bool inPruning = _inPruning != 0;
            return !inPruning && _basePruningStrategy.DeleteObsoleteKeys;
        }
    }

    public bool ShouldPruneDirtyNode(TrieStoreState state)
    {
        bool inPruning = _inPruning != 0;
        if (inPruning)
        {
            // Make it take snapshot regularly as full pruning need the best persisted state to change.
            // Only blocks older than the pruning boundary can be persisted, so measure from the boundary and not
            // from the head: the head is always at least `pruningBoundary` (>= 64) ahead of the last persisted
            // block, so a head-relative check is always true and forces a snapshot on every block.
            ulong reorgBoundary = state.LatestCommittedBlock.SaturatingSub(_pruningBoundary);
            if (reorgBoundary.SaturatingSub(state.LastPersistedBlock) > SnapshotIntervalDuringFullPruning) return true;
        }
        return _basePruningStrategy.ShouldPruneDirtyNode(state);
    }

    public bool ShouldPrunePersistedNode(TrieStoreState state) => _basePruningStrategy.ShouldPrunePersistedNode(state);

    /// <inheritdoc/>
    public void Dispose()
    {
        _fullPruningDb.PruningStarted -= OnPruningStarted;
        _fullPruningDb.PruningFinished -= OnPruningFinished;
    }
}
