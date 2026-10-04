// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Simulate;

/// <summary>
/// Serves the node's EIP-8304 index tables to a simulation while keeping the simulation's own tables apart.
/// </summary>
/// <remarks>
/// The base store is the node's live one, so every mutator must stay on the local store.
/// </remarks>
public class SimulateIndexTableStore(IIndexTableStore? baseStore) : IIndexTableStore, IClearableCache
{
    private IndexTableStore _localStore = new();

    public void Store(int level, long firstBlock, IReadOnlyList<IndexEntry> sortedEntries, Hash256? blockHash = null) =>
        _localStore.Store(level, firstBlock, sortedEntries, blockHash);

    public IReadOnlyList<IndexEntry>? Get(int level, long firstBlock, Hash256? blockHash = null) =>
        _localStore.Get(level, firstBlock, blockHash) ?? baseStore?.Get(level, firstBlock, blockHash);

    public void Remove(int level, long firstBlock, Hash256? blockHash = null) =>
        _localStore.Remove(level, firstBlock, blockHash);

    public void ClearCache() => _localStore = new();
}
