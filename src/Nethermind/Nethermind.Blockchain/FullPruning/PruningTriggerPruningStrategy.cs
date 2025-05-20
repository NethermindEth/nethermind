// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Db.FullPruning;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain.FullPruning;

public class PruningTriggerPruningStrategy: IPruningStrategy, IDisposable
{
    private readonly IFullPruningDb _fullPruningDb;
    private readonly IPruningStrategy _basePruningStrategy;
    private int _inPruning = 0;

    public PruningTriggerPruningStrategy(
        IFullPruningDb fullPruningDb,
        IPruningStrategy basePruningStrategy)
    {
        _fullPruningDb = fullPruningDb;
        _basePruningStrategy = basePruningStrategy;
        _fullPruningDb.PruningFinished += OnPruningFinished;
        _fullPruningDb.PruningStarted += OnPruningStarted;
    }

    private void OnPruningStarted(object? sender, EventArgs e)
    {
        Interlocked.CompareExchange(ref _inPruning, 1, 0);
    }

    private void OnPruningFinished(object? sender, EventArgs e)
    {
        Interlocked.CompareExchange(ref _inPruning, 0, 1);
    }

    public bool DeleteObsoleteKeys => _basePruningStrategy.DeleteObsoleteKeys;

    public bool ShouldPruneDirtyNode(TrieStoreState state)
    {
        bool inPruning = _inPruning != 0;
        if (inPruning)
        {
            // `ShouldPersist` would cause all block to be persisted which is good, but we also need to trigger
            // snapshot also, which normally only happens
            if (state.LatestCommittedBlock % 8 == 0)
            {
                return true;
            }
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
