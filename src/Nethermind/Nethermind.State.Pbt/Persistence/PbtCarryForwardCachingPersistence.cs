// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Concurrent;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>
/// <see cref="IPbtPersistence"/> decorator that caches persisted account and slot-run reads across heads, so a
/// new head does not re-read the serving working set from the database. Wraps the reader (serve/fill) and the
/// write batch (drops the committed write-set). Generation-gated: a reader behind the cache basis bypasses it rather
/// than serving stale data.
/// </summary>
public sealed class PbtCarryForwardCachingPersistence : IPbtPersistence
{
    public const int DefaultMaxEntriesPerKind = 262144;

    private readonly IPbtPersistence _inner;
    private readonly int _maxEntriesPerKind;

    private ConcurrentDictionary<ValueHash256, PbtAccount?> _accounts;
    private ConcurrentDictionary<PbtPath, PackedSlotRun> _headerRuns;
    private ConcurrentDictionary<PbtStoragePath, PackedSlotRun> _storageRuns;
    private int _accountCount;
    private int _runCount;

    private readonly Lock _lock = new();
    private StateId _basis;
    private long _generation;

    // Handed from one write batch to the next, so a persist does not grow fresh sets on the LOH. A spare keeps the
    // capacity of the batch that filled it for the node's lifetime, so only sets within the cache's own per-kind
    // capacity are kept.
    private HashSet<ValueHash256>? _spareWrittenAccounts;
    private HashSet<PbtPath>? _spareWrittenHeaderRuns;
    private HashSet<PbtStoragePath>? _spareWrittenStorageRuns;

    private HashSet<ValueHash256> RentWrittenAccounts() => Interlocked.Exchange(ref _spareWrittenAccounts, null) ?? [];

    private HashSet<PbtPath> RentWrittenHeaderRuns() => Interlocked.Exchange(ref _spareWrittenHeaderRuns, null) ?? [];

    private HashSet<PbtStoragePath> RentWrittenStorageRuns() => Interlocked.Exchange(ref _spareWrittenStorageRuns, null) ?? [];

    private void ReturnWrittenSets(HashSet<ValueHash256>? writtenAccounts, HashSet<PbtPath>? writtenHeaderRuns, HashSet<PbtStoragePath>? writtenStorageRuns)
    {
        ReturnWrittenSet(ref _spareWrittenAccounts, writtenAccounts);
        ReturnWrittenSet(ref _spareWrittenHeaderRuns, writtenHeaderRuns);
        ReturnWrittenSet(ref _spareWrittenStorageRuns, writtenStorageRuns);
    }

    private void ReturnWrittenSet<TKey>(ref HashSet<TKey>? spare, HashSet<TKey>? written)
    {
        if (written is null || written.Count > _maxEntriesPerKind) return;
        written.Clear();
        Volatile.Write(ref spare, written);
    }

    public PbtCarryForwardCachingPersistence(IPbtPersistence inner, int maxEntriesPerKind)
    {
        _inner = inner;
        _maxEntriesPerKind = maxEntriesPerKind;
        _accounts = NewAccountCache();
        _headerRuns = NewRunCache<PbtPath>();
        _storageRuns = NewRunCache<PbtStoragePath>();
        using IPbtPersistence.IReader reader = inner.CreateReader();
        _basis = reader.CurrentState;
    }

    public IPbtPersistence.IReader CreateReader()
    {
        IPbtPersistence.IReader reader = _inner.CreateReader();
        long generation;
        bool atBasis;
        using (_lock.EnterScope())
        {
            atBasis = reader.CurrentState == _basis;
            generation = _generation;
        }
        return atBasis ? new CachingReader(this, reader, generation) : reader;
    }

    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 treeRoot) =>
        new InvalidatingWriteBatch(this, _inner.CreateWriteBatch(from, to, treeRoot), to);

    public void ClearCaches()
    {
        _inner.ClearCaches();
        using IPbtPersistence.IReader reader = _inner.CreateReader();
        OnCommitted(reader.CurrentState, writtenAccounts: null, writtenHeaderRuns: null, writtenStorageRuns: null, clearAll: true);
    }

    // Sized for the cap up front: a ConcurrentDictionary grows by re-creating every entry, which from the default
    // capacity takes a dozen rounds on the way to the cap, the last ones tens of megabytes on whichever reader adds
    // the entry that crosses a threshold. Clear() would shrink it back to the default, so a wipe swaps in a new one.
    // One lock: every write already runs under _lock, and a dictionary grows once any one lock passes its share of
    // the buckets, so with more locks the fullest one would still trigger a growth just short of the cap.
    private ConcurrentDictionary<ValueHash256, PbtAccount?> NewAccountCache() => new(concurrencyLevel: 1, _maxEntriesPerKind);

    private ConcurrentDictionary<TKey, PackedSlotRun> NewRunCache<TKey>() where TKey : struct, IPbtKey<TKey> => new(concurrencyLevel: 1, _maxEntriesPerKind);

    private bool IsCurrent(long readerGeneration) => Volatile.Read(ref _generation) == readerGeneration;

    private ConcurrentDictionary<TKey, PackedSlotRun> Runs<TKey>() where TKey : struct, IPbtKey<TKey> =>
        SlotRun.ByZone<TKey, ConcurrentDictionary<TKey, PackedSlotRun>>(_headerRuns, _storageRuns);

    private void TryCacheAccount(in ValueHash256 addressHash, PbtAccount? account, long readerGeneration)
    {
        if (_accounts.ContainsKey(addressHash)) return;
        using (_lock.EnterScope())
        {
            if (_generation != readerGeneration) return;
            if (_accounts.ContainsKey(addressHash)) return;
            if (_accountCount >= _maxEntriesPerKind)
            {
                _accounts = NewAccountCache();
                _accountCount = 0;
            }
            if (_accounts.TryAdd(addressHash, account)) _accountCount++;
        }
    }

    // Cached runs are never returned to their pool: a concurrent reader may still be cloning an evicted one.
    private void TryCacheRun<TKey>(in TKey runKey, PackedSlotRun run, long readerGeneration) where TKey : struct, IPbtKey<TKey>
    {
        // Best effort: under many threads the reads would otherwise queue here, and a skipped insert only costs a later miss.
        if (!_lock.TryEnter()) return;
        try
        {
            if (_generation != readerGeneration) return;
            if (Runs<TKey>().ContainsKey(runKey)) return;
            if (_runCount >= _maxEntriesPerKind) ClearRuns();
            if (Runs<TKey>().TryAdd(runKey, run)) _runCount++;
        }
        finally
        {
            _lock.Exit();
        }
    }

    private void ClearRuns()
    {
        _headerRuns = NewRunCache<PbtPath>();
        _storageRuns = NewRunCache<PbtStoragePath>();
        _runCount = 0;
    }

    private void OnCommitted(StateId to, HashSet<ValueHash256>? writtenAccounts, HashSet<PbtPath>? writtenHeaderRuns,
        HashSet<PbtStoragePath>? writtenStorageRuns, bool clearAll)
    {
        using (_lock.EnterScope())
        {
            _generation++;
            _basis = to;
            if (clearAll)
            {
                // An empty cache keeps its tables, so a clear with nothing cached allocates nothing. The counts are exact under _lock.
                if (_accountCount != 0) _accounts = NewAccountCache();
                _accountCount = 0;
                if (_runCount != 0) ClearRuns();
                return;
            }
            if (writtenAccounts is not null)
                foreach (ValueHash256 addressHash in writtenAccounts)
                    if (_accounts.TryRemove(addressHash, out _)) _accountCount--;
            RemoveRuns(_headerRuns, writtenHeaderRuns);
            RemoveRuns(_storageRuns, writtenStorageRuns);
        }
    }

    private void RemoveRuns<TKey>(ConcurrentDictionary<TKey, PackedSlotRun> runs, HashSet<TKey>? writtenRuns) where TKey : struct, IPbtKey<TKey>
    {
        if (writtenRuns is null) return;
        foreach (TKey runKey in writtenRuns)
            if (runs.TryRemove(runKey, out _)) _runCount--;
    }

    private sealed class CachingReader(PbtCarryForwardCachingPersistence parent, IPbtPersistence.IReader inner, long generation) : IPbtPersistence.IReader
    {
        public StateId CurrentState => inner.CurrentState;
        public ValueHash256 CurrentRoot => inner.CurrentRoot;

        public PbtAccount? GetAccount(in ValueHash256 addressHash)
        {
            bool current = parent.IsCurrent(generation);
            if (current && parent._accounts.TryGetValue(addressHash, out PbtAccount? cached))
            {
                // Checked again after the lookup: the cache can hold an entry filled after this reader's generation ended.
                if (parent.IsCurrent(generation)) return cached;
                current = false;
            }

            PbtAccount? account = inner.GetAccount(addressHash);
            if (current) parent.TryCacheAccount(addressHash, account, generation);
            return account;
        }

        public PackedSlotRun GetSlotRun<TKey>(in TKey runKey) where TKey : struct, IPbtKey<TKey>
        {
            ConcurrentDictionary<TKey, PackedSlotRun> runs = parent.Runs<TKey>();
            bool current = parent.IsCurrent(generation);
            if (current && runs.TryGetValue(runKey, out PackedSlotRun? cached))
            {
                // Checked again after the lookup: the cache can hold an entry filled after this reader's generation ended.
                if (parent.IsCurrent(generation)) return cached.Clone();
                current = false;
            }

            PackedSlotRun run = inner.GetSlotRun(runKey);
            if (current && !runs.ContainsKey(runKey)) parent.TryCacheRun(runKey, run.Clone(), generation);
            return run;
        }

        public CodeInfo? GetCode(in ValueHash256 codeHash) => inner.GetCode(codeHash);
        public bool TryLoadNodeGroup<TPath, TArg>(in TPath groupKey, ReadOnlySpanAction<byte, TArg> action, TArg arg)
            where TPath : struct, IPbtNodePath<TPath> where TArg : allows ref struct => inner.TryLoadNodeGroup(groupKey, action, arg);
        public void Dispose() => inner.Dispose();
    }

    private sealed class InvalidatingWriteBatch(PbtCarryForwardCachingPersistence parent, IPbtPersistence.IWriteBatch inner, StateId to) : IPbtPersistence.IWriteBatch
    {
        private HashSet<ValueHash256>? _writtenAccounts;
        private HashSet<PbtPath>? _writtenHeaderRuns;
        private HashSet<PbtStoragePath>? _writtenStorageRuns;

        public void SetAccount(in ValueHash256 addressHash, PbtAccount? account)
        {
            (_writtenAccounts ??= parent.RentWrittenAccounts()).Add(addressHash);
            inner.SetAccount(addressHash, account);
        }

        public void SetSlotRun<TKey>(in TKey runKey, PackedSlotRun run) where TKey : struct, IPbtKey<TKey>
        {
            SlotRun.ByZone<TKey, HashSet<TKey>>(_writtenHeaderRuns ??= parent.RentWrittenHeaderRuns(), _writtenStorageRuns ??= parent.RentWrittenStorageRuns()).Add(runKey);
            inner.SetSlotRun(runKey, run);
        }

        public void SetCode(in ValueHash256 codeHash, CodeInfo code) => inner.SetCode(codeHash, code);

        public void SetNodeGroup<TPath>(TPath groupKey, scoped ReadOnlySpan<byte> payload) where TPath : struct, IPbtNodePath<TPath> => inner.SetNodeGroup(groupKey, payload);

        public void Commit()
        {
            inner.Commit();
            parent.OnCommitted(to, _writtenAccounts, _writtenHeaderRuns, _writtenStorageRuns, clearAll: false);
        }

        public void Dispose()
        {
            inner.Dispose();
            parent.ReturnWrittenSets(_writtenAccounts, _writtenHeaderRuns, _writtenStorageRuns);
            _writtenAccounts = null;
            _writtenHeaderRuns = null;
            _writtenStorageRuns = null;
        }
    }
}
