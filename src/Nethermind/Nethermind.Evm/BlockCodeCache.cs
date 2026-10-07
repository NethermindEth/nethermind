// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>An <see cref="ICodeCache"/> that also keeps the code a block loads until <see cref="ClearBlock"/>.</summary>
/// <remarks>
/// A warm CALL costs 100 gas whatever the size of the code, so a block can call more distinct large contracts, round
/// and round, than the process-wide cache holds; each call would then read and analyse its code again. Code loaded
/// during the block is kept here, up to a cap on the memory it retains. Past the cap, a running transaction reclaims
/// entries whose latest recorded user is no longer running, including code loaded by warming; evicting what running
/// transactions use instead would miss on every load of a cycle over a set larger than the cache. Retention is best
/// effort: a later user that finishes first, or a hit racing a sweep, can leave an earlier running transaction to
/// reload shared code once. Warmth does not outlive a transaction, so each such eviction takes another transaction
/// paying cold access for that code, and earlier transactions cannot fill the cap to leave a later one without retention.
/// The process-wide cache is probed first, so a block it serves pays nothing here.
/// </remarks>
public sealed class BlockCodeCache : ICodeCache
{
    /// <summary>The default cap on retained memory: about 14k contracts of 64 KiB.</summary>
    public static readonly long DefaultMaxBytes = 1.GiB;

    /// <summary>Charged per entry on top of its code and jump-destination bitmap: the objects, padding and dictionary entry.</summary>
    internal const int EntryOverheadBytes = 256;

    /// <summary>The transaction executing on this thread, or 0 outside one.</summary>
    [ThreadStatic] private static long _currentTransaction;

    private static long _lastTransaction;

    private readonly ICodeCache _inner;
    private readonly long _maxBytes;
    private readonly Retained _retained;

    /// <param name="inner">The process-wide cache.</param>
    public BlockCodeCache(ICodeCache inner) : this(inner, DefaultMaxBytes) { }

    /// <param name="inner">The process-wide cache.</param>
    /// <param name="maxBytes">The most memory the block's code retains, in bytes.</param>
    public BlockCodeCache(ICodeCache inner, long maxBytes) : this(inner, maxBytes, new Retained()) { }

    private BlockCodeCache(ICodeCache inner, long maxBytes, Retained retained)
    {
        _inner = inner;
        _maxBytes = maxBytes;
        _retained = retained;
    }

    /// <summary>The memory charged for the block's code, in bytes.</summary>
    internal long Bytes => Volatile.Read(ref _retained.Bytes);

    /// <summary>A view of the same block's code that takes code in only while the block retains less than <paramref name="maxBytes"/>.</summary>
    /// <remarks>Lets warming share the block's code without spending the budget of the block's own execution.</remarks>
    public BlockCodeCache WithLimit(long maxBytes) => new(_inner, maxBytes, _retained);

    /// <summary>Marks the calling thread as executing a block transaction until the returned scope is disposed.</summary>
    /// <remarks>Code loaded outside a transaction, as warming does, is kept too, but gives way to a transaction's code past the cap.</remarks>
    public TransactionScope BeginTransaction()
    {
        long transaction;
        lock (_retained.Running)
        {
            transaction = Interlocked.Increment(ref _lastTransaction);
            _retained.Running.Add(transaction);
        }

        long previous = _currentTransaction;
        _currentTransaction = transaction;
        return new TransactionScope(this, transaction, previous);
    }

    public CodeInfo? Get(in ValueHash256 codeHash)
    {
        CodeInfo? codeInfo = _inner.Get(in codeHash);
        if (codeInfo is not null || Volatile.Read(ref _retained.Bytes) == 0 || !_retained.Code.TryGetValue(codeHash, out Entry? entry))
        {
            return codeInfo;
        }

        Record(entry);
        return entry.Code;
    }

    public void Set(in ValueHash256 codeHash, CodeInfo codeInfo)
    {
        _inner.Set(in codeHash, codeInfo);

        // Another transaction missing on the same code may have loaded it first.
        if (_retained.Code.TryGetValue(codeHash, out Entry? loaded))
        {
            Record(loaded);
            return;
        }

        // Racing loads may each pass the check, overshooting the cap by at most one code per thread.
        long charge = codeInfo.CodeLength + (codeInfo.CodeLength >> 3) + EntryOverheadBytes;
        long transaction = _currentTransaction;
        if (Volatile.Read(ref _retained.Bytes) + charge > _maxBytes && (transaction == 0 || !_retained.TryMakeRoom(charge, _maxBytes)))
        {
            return;
        }

        Entry entry = new(codeInfo, charge, transaction);
        loaded = _retained.Code.GetOrAdd(codeHash, entry);
        if (ReferenceEquals(loaded, entry))
        {
            Interlocked.Add(ref _retained.Bytes, charge);
        }
        else
        {
            Record(loaded);
        }
    }

    private static void Record(Entry entry)
    {
        long transaction = _currentTransaction;
        if (transaction != 0 && entry.Transaction != transaction) entry.Transaction = transaction;
    }

    /// <summary>Drops the code kept for the block, for every view of it.</summary>
    public void ClearBlock()
    {
        _retained.Code.Clear();
        Volatile.Write(ref _retained.Bytes, 0);
    }

    public void Clear()
    {
        ClearBlock();
        _inner.Clear();
    }

    private void EndTransaction(long transaction, long previous)
    {
        _currentTransaction = previous;
        lock (_retained.Running)
        {
            _retained.Running.Remove(transaction);
        }
    }

    public readonly struct TransactionScope : IDisposable
    {
        private readonly BlockCodeCache _cache;
        private readonly long _transaction;
        private readonly long _previous;

        internal TransactionScope(BlockCodeCache cache, long transaction, long previous)
        {
            _cache = cache;
            _transaction = transaction;
            _previous = previous;
        }

        public void Dispose() => _cache.EndTransaction(_transaction, _previous);
    }

    private sealed class Entry(CodeInfo code, long charge, long transaction)
    {
        public readonly CodeInfo Code = code;
        public readonly long Charge = charge;

        /// <summary>The latest transaction to use the code, or 0 if only code outside a transaction has.</summary>
        public long Transaction = transaction;
    }

    private sealed class Retained
    {
        public readonly ConcurrentDictionary<ValueHash256, Entry> Code = new();
        public readonly HashSet<long> Running = [];
        private readonly Lock _sweepLock = new();
        public long Bytes;

        /// <summary>The transactions running at the last sweep, if it evicted nothing.</summary>
        /// <remarks>Until one of them finishes, another sweep could only evict what a later transaction took over by a hit.</remarks>
        private HashSet<long>? _fruitlessSweep;

        /// <summary>Evicts entries whose latest recorded user is not running, unless no transaction running at the last fruitless sweep has finished since.</summary>
        /// <returns>Whether <paramref name="charge"/> now fits under <paramref name="maxBytes"/>.</returns>
        public bool TryMakeRoom(long charge, long maxBytes)
        {
            if (!_sweepLock.TryEnter()) return false;
            try
            {
                // Transactions are numbered under this lock, so every one up to lastTransaction is in the snapshot or has
                // finished, and later ones are kept. A hit can still stamp an entry after the loop reads it; that entry is
                // evicted and its transaction reloads it once.
                long lastTransaction;
                HashSet<long> running;
                lock (Running)
                {
                    if (_fruitlessSweep?.IsSubsetOf(Running) == true) return false;
                    lastTransaction = Volatile.Read(ref _lastTransaction);
                    running = [.. Running];
                }

                bool evicted = false;
                foreach (KeyValuePair<ValueHash256, Entry> pair in Code)
                {
                    long transaction = pair.Value.Transaction;
                    if (transaction <= lastTransaction && !running.Contains(transaction) && Code.TryRemove(pair))
                    {
                        Interlocked.Add(ref Bytes, -pair.Value.Charge);
                        evicted = true;
                    }
                }

                _fruitlessSweep = evicted ? null : running;
                return Volatile.Read(ref Bytes) + charge <= maxBytes;
            }
            finally
            {
                _sweepLock.Exit();
            }
        }
    }
}
