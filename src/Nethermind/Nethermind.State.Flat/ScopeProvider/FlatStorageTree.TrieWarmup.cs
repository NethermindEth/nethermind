// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed partial class FlatStorageTree
{
    private StorageTree? _cohortWarmupTree;

    internal FlatWorldStateScope WarmupScope => _scope;
    internal Hash256 WarmupAddress => AddressHash;

    internal PatriciaTree GetCohortWarmupTree()
    {
        Trees? trees = Volatile.Read(ref _trees);
        if (trees is not null) return trees.Warmup;
        StorageTree? existing = Volatile.Read(ref _cohortWarmupTree);
        if (existing is not null) return existing;
        StorageTree created = new(new StorageTrieStoreWarmerAdapter(_bundle, AddressHash), _storageRoot, _logManager);
        return Interlocked.CompareExchange(ref _cohortWarmupTree, created, null) ?? created;
    }

    internal bool IsWarmupStopped(int sequenceId) => _scope.IsWarmupStopped(sequenceId);

    internal void WarmUpStorageTrieBatch(ReadOnlySpan<PatriciaTree.WarmUpEntry> entries, int sequenceId)
    {
        if (_scope.IsWarmupStopped(sequenceId) || !_bundle.TryLeaseReadOnlyBundle()) return;
        try
        {
            _scope.WarmUpTrieBatch(GetTrees().Warmup, entries, sequenceId);
        }
        finally
        {
            _bundle.ReleaseReadOnlyBundleLease();
        }
    }

    internal void CompleteWarmups(int count) => _scope.CompleteWarmups(count);
}
