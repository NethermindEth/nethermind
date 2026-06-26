// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using IScope = Nethermind.Evm.State.IWorldStateScopeProvider.IScope;
using IStorageTree = Nethermind.Evm.State.IWorldStateScopeProvider.IStorageTree;
using ICodeDb = Nethermind.Evm.State.IWorldStateScopeProvider.ICodeDb;
using IWorldStateWriteBatch = Nethermind.Evm.State.IWorldStateScopeProvider.IWorldStateWriteBatch;
using IAsyncBalReaderSink = Nethermind.Evm.State.IWorldStateScopeProvider.IAsyncBalReaderSink;

namespace Nethermind.State;

/// <summary>
/// Decorates a read-only <see cref="IWorldStateScopeProvider"/> with the cross-block
/// <see cref="HeadStateCache"/>. When the scope's base block is the head (or one of the tracked
/// ancestors) reads are served as O(1) cache lookups instead of trie traversals; otherwise the scope
/// is a pure passthrough. Writes are never cached — the cache is maintained solely by
/// <see cref="HeadStateCacheUpdater"/> and by read backfill.
/// </summary>
public sealed class HeadStateCacheScopeProvider(IWorldStateScopeProvider baseProvider, HeadStateCache cache)
    : IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
    {
        IScope baseScope = baseProvider.BeginScope(baseBlock, metrics);
        return cache.TrySnapshot(baseBlock?.Hash, out HeadStateSnapshot snapshot)
            ? new CachingScope(baseScope, cache, snapshot)
            : baseScope; // passthrough: historical, side-branch, too-deep, or mid-update
    }

    private sealed class CachingScope(IScope baseScope, HeadStateCache cache, HeadStateSnapshot snapshot) : IScope
    {
        private readonly SeqlockCache<AddressAsKey, Account> _accounts = cache.Accounts;

        public Account? Get(Address address)
        {
            AddressAsKey key = address;
            if (!snapshot.IsCurrent || snapshot.ChangedInWindow(in key))
            {
                return baseScope.Get(address);
            }

            if (_accounts.TryGetValue(in key, out Account? cached) && snapshot.IsCurrent)
            {
                return cached;
            }

            // Miss (or raced an advance): load at the pinned header root and backfill. The backfill is
            // generation-guarded inside the cache, so a value loaded at the old head is dropped (not
            // published) if an advance happened meanwhile.
            Account? loaded = baseScope.Get(address);
            cache.TryBackfillAccount(in key, loaded, snapshot.Generation);
            return loaded;
        }

        public IStorageTree CreateStorageTree(Address address) =>
            new CachingStorageTree(baseScope.CreateStorageTree(address), cache, address, snapshot);

        // --- Everything below is a straight delegation to the base scope. ---

        public Hash256 RootHash => baseScope.RootHash;
        public void UpdateRootHash() => baseScope.UpdateRootHash();
        public ICodeDb CodeDb => baseScope.CodeDb;
        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);
        public IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => baseScope.StartWriteBatch(estimatedAccountNum);
        public void Commit(ulong blockNumber) => baseScope.Commit(blockNumber);
        public Task HintBal(ReadOnlyBlockAccessList bal, IAsyncBalReaderSink? sink = null) => baseScope.HintBal(bal, sink);
        public void Dispose() => baseScope.Dispose();
    }

    private sealed class CachingStorageTree(
        IStorageTree baseStorageTree,
        HeadStateCache cache,
        Address address,
        HeadStateSnapshot snapshot) : IStorageTree
    {
        private readonly SeqlockCache<StorageCell, byte[]> _storage = cache.Storage;

        public byte[] Get(in UInt256 index)
        {
            StorageCell cell = new(address, in index);
            if (!snapshot.IsCurrent || snapshot.ChangedInWindow(in cell))
            {
                return baseStorageTree.Get(index);
            }

            if (_storage.TryGetValue(in cell, out byte[]? cached) && snapshot.IsCurrent)
            {
                return cached!;
            }

            byte[] loaded = baseStorageTree.Get(index);
            cache.TryBackfillStorage(in cell, loaded, snapshot.Generation);
            return loaded;
        }

        public Hash256 RootHash => baseStorageTree.RootHash;
        public void HintSet(in UInt256 index, byte[]? value) => baseStorageTree.HintSet(in index, value);
        public byte[] Get(in ValueHash256 hash) => baseStorageTree.Get(in hash);
    }
}
