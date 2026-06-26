// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
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
/// Observes the main processing world state's write batches to record, per block, the storage cells
/// that changed — captured at commit time from <see cref="IStorageWriteBatch.Set"/> (which sees only
/// real writes, before the journal normalizes them). Records into a <see cref="HeadStateDeltaBuffer"/>
/// keyed by the post-commit state root, so the head cache can stay coherent on any node, independent
/// of EIP-7928/Block Access Lists. This is a pure observer: every call is forwarded unchanged.
/// </summary>
public sealed class HeadStateDeltaCaptureScopeProvider(IWorldStateScopeProvider baseProvider, HeadStateDeltaBuffer buffer)
    : IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics) =>
        new CaptureScope(baseProvider.BeginScope(baseBlock, metrics), buffer);

    private sealed class CaptureScope(IScope baseScope, HeadStateDeltaBuffer buffer) : IScope
    {
        // Cap on accumulated cells: a scope normally spans a single block near the head, but a long
        // sync branch reuses one scope across many blocks. Past the cap we stop tracking and signal a
        // flush rather than grow unbounded (the consumer then rebuilds lazily).
        private const int MaxTrackedSlots = 1 << 21;

        // Writes precede a block's commits and a scope spans one block in the live case, so the set is
        // accumulated per scope (not reset per commit). Over-inclusion across a multi-block branch is
        // safe: the consumer re-reads/defers the extra cells, which is correct, just less efficient.
        private readonly HashSet<StorageCell> _slots = [];
        private bool _requiresFlush;

        internal void RecordSlot(in StorageCell cell)
        {
            if (_requiresFlush) return;
            if (_slots.Count >= MaxTrackedSlots)
            {
                _requiresFlush = true;
                _slots.Clear();
                return;
            }
            _slots.Add(cell);
        }

        internal void RecordClear() => _requiresFlush = true;

        public void Commit(ulong blockNumber)
        {
            baseScope.Commit(blockNumber);

            // RootHash now reflects the committed state; the block's final commit stores under its
            // final state root. Snapshot so later commits don't mutate the stored set.
            buffer.Store(baseScope.RootHash, new HeadStateBlockDelta(_slots.ToFrozenSet(), _requiresFlush));
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
        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated
        {
            add => baseBatch.OnAccountUpdated += value;
            remove => baseBatch.OnAccountUpdated -= value;
        }

        public void Set(Address key, Account? account) => baseBatch.Set(key, account);

        public IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new CaptureStorageWriteBatch(baseBatch.CreateStorageWriteBatch(key, estimatedEntries), key, scope);

        public void Dispose() => baseBatch.Dispose();
    }

    private sealed class CaptureStorageWriteBatch(IStorageWriteBatch baseBatch, Address address, CaptureScope scope) : IStorageWriteBatch
    {
        public void Set(in UInt256 index, byte[] value)
        {
            scope.RecordSlot(new StorageCell(address, in index));
            baseBatch.Set(in index, value);
        }

        public void Clear()
        {
            // Self-destruct wipes all of the account's slots; their keys aren't enumerable here, so
            // signal a flush rather than risk leaving stale cached slots for this account.
            scope.RecordClear();
            baseBatch.Clear();
        }

        public void Dispose() => baseBatch.Dispose();
    }
}
