// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
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
/// once such a write lands reads what the main thread is about to read. The main thread only copies its journal
/// into a write set at commit; the prewarmer applies it to the overlay off the processing thread.
/// </remarks>
public partial class PreBlockCaches
{
    private readonly ConcurrentDictionary<AddressAsKey, Account?> _committedAccounts = new();
    private readonly ConcurrentDictionary<StorageCell, UInt256> _committedSlots = new();
    private readonly ConcurrentQueue<CommittedWriteSet> _committedWriteSets = new();
    private readonly SemaphoreSlim _commitSignal = new(0);
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

    /// <summary>Starts the write set of the transaction the main thread is committing.</summary>
    public CommittedWriteSet BeginWriteSet() => new(MainTxIndex);

    /// <summary>Queues a committed transaction's write set for the prewarmer; an empty one is dropped.</summary>
    public void Publish(CommittedWriteSet writes)
    {
        if (writes.IsEmpty)
        {
            writes.Dispose();
            return;
        }

        _committedWriteSets.Enqueue(writes);
        _commitSignal.Release();
    }

    /// <summary>Takes the next committed transaction's write set, in commit order; the caller disposes it.</summary>
    public bool TryDequeueCommitted([MaybeNullWhen(false)] out CommittedWriteSet writeSet) => _committedWriteSets.TryDequeue(out writeSet);

    /// <summary>Blocks until a write set is published, the timeout passes, or the token is cancelled.</summary>
    public bool WaitForCommit(TimeSpan timeout, CancellationToken token) => _commitSignal.Wait(timeout, token);

    /// <summary>Makes a committed write set visible to populator scopes.</summary>
    public void ApplyCommitted(CommittedWriteSet writes)
    {
        foreach ((AddressAsKey address, Account? account) in writes.Accounts) _committedAccounts[address] = account;
        foreach ((StorageCell cell, UInt256 value) in writes.Slots) _committedSlots[cell] = value;
    }

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

    /// <summary>The keys and values one committed transaction wrote.</summary>
    public sealed class CommittedWriteSet(int txIndex) : IDisposable
    {
        public int TxIndex { get; } = txIndex;
        public PooledList<(AddressAsKey Address, Account? Account)> Accounts { get; } = [];
        public PooledList<(StorageCell Cell, UInt256 Value)> Slots { get; } = [];

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
}
