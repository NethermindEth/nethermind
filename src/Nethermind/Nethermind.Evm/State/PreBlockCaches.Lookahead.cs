// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <summary>
/// Lookahead re-warming support: the block's committed values so far, served to populator scopes ahead of the
/// pre-block cache, and the per-transaction write sets that let the prewarmer find the speculative runs they
/// invalidate.
/// </summary>
/// <remarks>
/// Speculative warming runs every transaction against the parent state, so a transaction whose reads depend on
/// what an earlier one in the same block wrote warms the wrong keys. Re-running it against the committed values
/// once such a write lands reads what the main thread is about to read.
/// </remarks>
public partial class PreBlockCaches
{
    private readonly ConcurrentDictionary<AddressAsKey, Account?> _committedAccounts = new();
    private readonly ConcurrentDictionary<StorageCell, UInt256> _committedSlots = new();
    private readonly ConcurrentQueue<CommittedWriteSet> _committedWriteSets = new();
    private int _mainTxIndex = -1;

    [ThreadStatic]
    private static ReadSet? _currentReadSet;

    /// <summary>Index of the transaction the main thread is executing; stamped onto the write set it commits.</summary>
    public int MainTxIndex
    {
        get => Volatile.Read(ref _mainTxIndex);
        set => Volatile.Write(ref _mainTxIndex, value);
    }

    public bool TryGetCommitted(in AddressAsKey address, out Account? account) => _committedAccounts.TryGetValue(address, out account);

    public bool TryGetCommitted(in StorageCell cell, out UInt256 value) => _committedSlots.TryGetValue(cell, out value);

    /// <summary>Takes the next committed transaction's write set, in commit order; the caller disposes it.</summary>
    public bool TryDequeueCommitted([MaybeNullWhen(false)] out CommittedWriteSet writeSet) => _committedWriteSets.TryDequeue(out writeSet);

    /// <summary>Wraps the consumer scope's write batch so each committed transaction's writes reach the overlay and the queue.</summary>
    public IWorldStateScopeProvider.IWorldStateWriteBatch WrapCommittedWrites(IWorldStateScopeProvider.IWorldStateWriteBatch inner) =>
        new CommittedWriteBatch(this, inner);

    /// <summary>Starts recording the keys the current thread's populator scope reads until the set is disposed.</summary>
    /// <exception cref="InvalidOperationException">A read set is already being recorded on this thread.</exception>
    public ReadSet BeginReadSet()
    {
        if (_currentReadSet is not null)
        {
            throw new InvalidOperationException("Read sets must not nest; the previous one would be orphaned.");
        }

        return _currentReadSet = new ReadSet(this);
    }

    /// <summary>The read set being recorded on the current thread for this cache, if any.</summary>
    public ReadSet? CurrentReadSet
    {
        get
        {
            ReadSet? readSet = _currentReadSet;
            return readSet is not null && ReferenceEquals(readSet.Owner, this) ? readSet : null;
        }
    }

    private void ClearCommitted()
    {
        _committedAccounts.Clear();
        _committedSlots.Clear();
        while (_committedWriteSets.TryDequeue(out CommittedWriteSet? writeSet)) writeSet.Dispose();
        MainTxIndex = -1;
    }

    /// <summary>The keys one committed transaction wrote.</summary>
    public sealed class CommittedWriteSet(int txIndex) : IDisposable
    {
        public int TxIndex { get; } = txIndex;
        public PooledList<AddressAsKey> Accounts { get; } = [];
        public PooledList<StorageCell> Slots { get; } = [];

        public bool IsEmpty => Accounts.Count == 0 && Slots.Count == 0;

        public void Dispose()
        {
            Accounts.Dispose();
            Slots.Dispose();
        }
    }

    /// <summary>The keys one speculative transaction run read; records only on the thread that created it.</summary>
    public sealed class ReadSet : IDisposable
    {
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

        internal ReadSet(PreBlockCaches owner) => Owner = owner;

        internal PreBlockCaches Owner { get; }
        public PooledSet<AddressAsKey> Accounts { get; } = [];
        public PooledSet<StorageCell> Slots { get; } = [];

        public void RecordAccount(in AddressAsKey address)
        {
            if (Environment.CurrentManagedThreadId == _ownerThreadId) Accounts.Add(address);
        }

        public void RecordSlot(in StorageCell cell)
        {
            if (Environment.CurrentManagedThreadId == _ownerThreadId) Slots.Add(cell);
        }

        public void Dispose()
        {
            if (ReferenceEquals(_currentReadSet, this)) _currentReadSet = null;
            Accounts.Dispose();
            Slots.Dispose();
        }
    }

    private sealed class CommittedWriteBatch(PreBlockCaches caches, IWorldStateScopeProvider.IWorldStateWriteBatch inner)
        : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly CommittedWriteSet _writes = new(caches.MainTxIndex);

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated
        {
            add => inner.OnAccountUpdated += value;
            remove => inner.OnAccountUpdated -= value;
        }

        public bool AcceptsStorageWrites => inner.AcceptsStorageWrites;

        public void Set(Address key, Account? account)
        {
            inner.Set(key, account);
            AddressAsKey address = key;
            caches._committedAccounts[address] = account;
            _writes.Accounts.Add(address);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new CommittedStorageWriteBatch(caches, inner.CreateStorageWriteBatch(key, estimatedEntries), key, _writes);

        public void Dispose()
        {
            inner.Dispose();
            if (_writes.IsEmpty)
            {
                _writes.Dispose();
                return;
            }

            caches._committedWriteSets.Enqueue(_writes);
        }
    }

    private sealed class CommittedStorageWriteBatch(
        PreBlockCaches caches,
        IWorldStateScopeProvider.IStorageWriteBatch inner,
        Address address,
        CommittedWriteSet writes) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, in UInt256 value)
        {
            inner.Set(in index, in value);
            StorageCell cell = new(address, in index);
            caches._committedSlots[cell] = value;
            writes.Slots.Add(cell);
        }

        public void Clear()
        {
            inner.Clear();
            // A destroyed contract's committed slots must not serve speculation; the scan is rare (self-destruct only).
            foreach (KeyValuePair<StorageCell, UInt256> slot in caches._committedSlots)
            {
                if (slot.Key.Address == address) caches._committedSlots.TryRemove(slot.Key, out _);
            }
        }

        public void Dispose() => inner.Dispose();
    }
}
