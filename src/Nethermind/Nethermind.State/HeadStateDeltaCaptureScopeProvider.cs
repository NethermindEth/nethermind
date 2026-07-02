// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using IScope = Nethermind.Evm.State.IWorldStateScopeProvider.IScope;
using IStorageTree = Nethermind.Evm.State.IWorldStateScopeProvider.IStorageTree;
using ICodeDb = Nethermind.Evm.State.IWorldStateScopeProvider.ICodeDb;
using IWorldStateWriteBatch = Nethermind.Evm.State.IWorldStateScopeProvider.IWorldStateWriteBatch;
using IStorageWriteBatch = Nethermind.Evm.State.IWorldStateScopeProvider.IStorageWriteBatch;
using IAsyncBalReaderSink = Nethermind.Evm.State.IWorldStateScopeProvider.IAsyncBalReaderSink;

namespace Nethermind.State;

/// <summary>
/// Observes the main processing world state's write batches to record, per block, the accounts and
/// storage cells that changed — captured at commit time from <see cref="IWorldStateWriteBatch.Set"/> and
/// <see cref="IStorageWriteBatch.Set"/> (which see only real writes, before the journal normalizes them).
/// Records into a <see cref="HeadStateDeltaBuffer"/> keyed by <c>(blockNumber, post-state root)</c>, so the
/// head cache can stay coherent on any node, independent of EIP-7928/Block Access Lists, without borrowing
/// the pooled <see cref="Block.AccountChanges"/> list (which the TxPool concurrently disposes). This is a
/// pure observer: every call is forwarded unchanged.
/// </summary>
public sealed class HeadStateDeltaCaptureScopeProvider(IWorldStateScopeProvider baseProvider, HeadStateDeltaBuffer buffer)
    : IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics) =>
        new CaptureScope(baseProvider.BeginScope(baseBlock, metrics), buffer);

    private sealed class CaptureScope(IScope baseScope, HeadStateDeltaBuffer buffer) : IScope
    {
        // Cap on a single block's tracked cells: past it we stop tracking and signal a flush rather than
        // grow unbounded (the consumer then rebuilds lazily).
        private const int MaxTrackedSlots = 1 << 21;

        // A processing scope can span multiple blocks (a sync branch), so writes are bucketed per block.
        // "_pending*" accumulate the current commit's writes (write batches merge into them); at each
        // Commit they fold into the per-block "_block*" set, which is reset when the block number changes.
        // This keeps the snapshot work O(block) rather than O(branch²) and the delta per-block-accurate.
        private readonly HashSet<StorageCell> _pendingSlots = [];
        private readonly HashSet<AddressAsKey> _pendingAccounts = [];
        private readonly HashSet<StorageCell> _blockSlots = [];
        private readonly HashSet<AddressAsKey> _blockAccounts = [];
        private readonly Lock _lock = new();
        private long _currentBlockNumber = -1;
        private bool _pendingFlush;
        private bool _blockFlush;

        // Storage-root computation runs the per-contract write batches in parallel
        // (PersistentStorageProvider.UpdateRootHashesMultiThread), so merges are locked. Each batch
        // buffers its own keys single-threaded and merges once on dispose, keeping locking
        // per-batch rather than per-key.
        internal void MergeSlots(List<StorageCell> cells)
        {
            if (cells.Count == 0) return;
            lock (_lock)
            {
                if (_pendingFlush) return;
                foreach (StorageCell cell in cells)
                {
                    if (_pendingSlots.Count >= MaxTrackedSlots)
                    {
                        _pendingFlush = true;
                        _pendingSlots.Clear();
                        return;
                    }
                    _pendingSlots.Add(cell);
                }
            }
        }

        internal void MergeAccounts(List<AddressAsKey> addresses)
        {
            if (addresses.Count == 0) return;
            lock (_lock)
            {
                foreach (AddressAsKey address in addresses) _pendingAccounts.Add(address);
            }
        }

        internal void RecordClear()
        {
            lock (_lock) _pendingFlush = true;
        }

        public void Commit(ulong blockNumber)
        {
            baseScope.Commit(blockNumber);

            // Parallel storage workers have joined by the time Commit runs. Fold this commit's pending
            // writes into the current block's set (resetting it on a new block), then snapshot.
            FrozenSet<StorageCell> slots;
            FrozenSet<AddressAsKey> accounts;
            bool flush;
            lock (_lock)
            {
                if ((long)blockNumber != _currentBlockNumber)
                {
                    _blockSlots.Clear();
                    _blockAccounts.Clear();
                    _blockSlots.UnionWith(_pendingSlots);
                    _blockAccounts.UnionWith(_pendingAccounts);
                    _blockFlush = _pendingFlush;
                    _currentBlockNumber = (long)blockNumber;
                }
                else
                {
                    _blockSlots.UnionWith(_pendingSlots);
                    _blockAccounts.UnionWith(_pendingAccounts);
                    _blockFlush |= _pendingFlush;
                }

                _pendingSlots.Clear();
                _pendingAccounts.Clear();
                _pendingFlush = false;

                slots = _blockSlots.ToFrozenSet();
                accounts = _blockAccounts.ToFrozenSet();
                flush = _blockFlush;
            }
            buffer.Store(blockNumber, baseScope.RootHash, new HeadStateBlockDelta(accounts, slots, flush));
        }

        public IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new CaptureWriteBatch(baseScope.StartWriteBatch(estimatedAccountNum), this);

        // --- Straight delegation below. ---

        public Hash256 RootHash => baseScope.RootHash;
        public void UpdateRootHash() => baseScope.UpdateRootHash();
        public Account? Get(Address address) => baseScope.Get(address);
        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);
        public ICodeDb CodeDb => baseScope.CodeDb;
        public IStorageTree CreateStorageTree(Address address) => baseScope.CreateStorageTree(address);
        public Task HintBal(ReadOnlyBlockAccessList bal, IAsyncBalReaderSink? sink = null) => baseScope.HintBal(bal, sink);
        public void Dispose() => baseScope.Dispose();
    }

    private sealed class CaptureWriteBatch(IWorldStateWriteBatch baseBatch, CaptureScope scope) : IWorldStateWriteBatch
    {
        // Account writes on a batch are single-threaded (only storage-root computation is parallel), so
        // this list needs no locking; it is merged into the scope's shared set once on dispose.
        private readonly List<AddressAsKey> _accounts = [];

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated
        {
            add => baseBatch.OnAccountUpdated += value;
            remove => baseBatch.OnAccountUpdated -= value;
        }

        public void Set(Address key, Account? account)
        {
            // Every account written to the trie this block changed (balance/nonce/code or — via a moved
            // storage root — storage). Null means deletion, still a change.
            _accounts.Add(key);
            baseBatch.Set(key, account);
        }

        public IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new CaptureStorageWriteBatch(baseBatch.CreateStorageWriteBatch(key, estimatedEntries), key, scope);

        public void Dispose()
        {
            scope.MergeAccounts(_accounts);
            baseBatch.Dispose();
        }
    }

    private sealed class CaptureStorageWriteBatch(IStorageWriteBatch baseBatch, Address address, CaptureScope scope) : IStorageWriteBatch
    {
        // A single storage batch is written by one worker thread, so this list needs no locking;
        // it is merged into the scope's shared set once on dispose.
        private readonly List<StorageCell> _cells = [];

        public void Set(in UInt256 index, byte[] value)
        {
            _cells.Add(new StorageCell(address, in index));
            baseBatch.Set(in index, value);
        }

        public void Clear()
        {
            // Self-destruct wipes all of the account's slots; their keys aren't enumerable here, so
            // signal a flush rather than risk leaving stale cached slots for this account.
            scope.RecordClear();
            baseBatch.Clear();
        }

        public void Dispose()
        {
            scope.MergeSlots(_cells);
            baseBatch.Dispose();
        }
    }
}
