// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.State.Flat;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>
/// <see cref="IPbtPersistence"/> decorator that caches persisted account and slot-run reads across heads, so a
/// new head does not re-read the serving working set from the database. Wraps the reader (serve/fill) and the
/// write batch (drops the committed write-set). Generation-gated: a reader behind the cache basis bypasses it rather
/// than serving stale data.
/// </summary>
public sealed class PbtCarryForwardCachingPersistence : IPbtPersistence
{
    private const int DefaultMaxEntriesPerKind = 262144;

    private readonly IPbtPersistence _inner;
    private readonly int _maxEntriesPerKind;

    private readonly ConcurrentDictionary<ValueHash256, PbtAccount?> _accounts = new();
    private readonly ConcurrentDictionary<PbtPath, PackedSlotRun> _headerRuns = new();
    private readonly ConcurrentDictionary<PbtStoragePath, PackedSlotRun> _storageRuns = new();
    private int _accountCount;
    private int _runCount;

    private readonly Lock _lock = new();
    private StateId _basis;
    private long _generation;

    public PbtCarryForwardCachingPersistence(IPbtPersistence inner, int maxEntriesPerKind = DefaultMaxEntriesPerKind)
    {
        _inner = inner;
        _maxEntriesPerKind = maxEntriesPerKind;
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

    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 treeRoot, WriteFlags flags) =>
        new InvalidatingWriteBatch(this, _inner.CreateWriteBatch(from, to, treeRoot, flags), to);

    public void Flush() => _inner.Flush();

    public void ClearCaches()
    {
        _inner.ClearCaches();
        using IPbtPersistence.IReader reader = _inner.CreateReader();
        OnCommitted(reader.CurrentState, writtenAccounts: null, writtenHeaderRuns: null, writtenStorageRuns: null, clearAll: true);
    }

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
                _accounts.Clear();
                _accountCount = 0;
            }
            if (_accounts.TryAdd(addressHash, account)) _accountCount++;
        }
    }

    // Cached runs are never returned to their pool: a concurrent reader may still be cloning an evicted one.
    private void TryCacheRun<TKey>(in TKey runKey, PackedSlotRun run, long readerGeneration) where TKey : struct, IPbtKey<TKey>
    {
        ConcurrentDictionary<TKey, PackedSlotRun> runs = Runs<TKey>();
        using (_lock.EnterScope())
        {
            if (_generation != readerGeneration) return;
            if (runs.ContainsKey(runKey)) return;
            if (_runCount >= _maxEntriesPerKind) ClearRuns();
            if (runs.TryAdd(runKey, run)) _runCount++;
        }
    }

    private void ClearRuns()
    {
        _headerRuns.Clear();
        _storageRuns.Clear();
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
                _accounts.Clear();
                _accountCount = 0;
                ClearRuns();
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
        public bool TryGetCodeLeaf(in PbtPath key, out ValueHash256 value) => inner.TryGetCodeLeaf(key, out value);
        public IEnumerator<KeyValuePair<ValueHash256, PbtAccount>> EnumerateAccounts() => inner.EnumerateAccounts();
        public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath> => inner.GetNodeGroup(groupKey);
        public RefCountingMemory?[] GetNodeGroups<TPath>(TPath[] groupKeys) where TPath : struct, IPbtNodePath<TPath> => inner.GetNodeGroups(groupKeys);
        public void Dispose() => inner.Dispose();
    }

    private sealed class InvalidatingWriteBatch(PbtCarryForwardCachingPersistence parent, IPbtPersistence.IWriteBatch inner, StateId to) : IPbtPersistence.IWriteBatch
    {
        private HashSet<ValueHash256>? _writtenAccounts;
        private HashSet<PbtPath>? _writtenHeaderRuns;
        private HashSet<PbtStoragePath>? _writtenStorageRuns;

        public void SetAccount(in ValueHash256 addressHash, PbtAccount? account)
        {
            (_writtenAccounts ??= []).Add(addressHash);
            inner.SetAccount(addressHash, account);
        }

        public void SetSlotRun<TKey>(in TKey runKey, PackedSlotRun run) where TKey : struct, IPbtKey<TKey>
        {
            SlotRun.ByZone<TKey, HashSet<TKey>>(_writtenHeaderRuns ??= [], _writtenStorageRuns ??= []).Add(runKey);
            inner.SetSlotRun(runKey, run);
        }

        public void SetCode(in ValueHash256 codeHash, CodeInfo code) => inner.SetCode(codeHash, code);
        public void SetCodeLeaf(in PbtPath key, in ValueHash256 value) => inner.SetCodeLeaf(key, value);

        public void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath> => inner.SetNodeGroup(groupKey, payload);

        public void Commit()
        {
            inner.Commit();
            parent.OnCommitted(to, _writtenAccounts, _writtenHeaderRuns, _writtenStorageRuns, clearAll: false);
        }

        public void Dispose() => inner.Dispose();
    }
}
