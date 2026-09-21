// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>
/// <see cref="IPbtPersistence"/> decorator that caches persisted account and slot-run reads across heads, so a
/// new head does not re-read the serving working set from the database. Wraps the reader (serve/fill) and the
/// write batch (drops the committed write-set; clears on storage clear or any staging write). Generation-gated:
/// a reader behind the cache basis bypasses it rather than serving stale data.
/// </summary>
public sealed class PbtCarryForwardCachingPersistence : IPbtPersistence
{
    private const int DefaultMaxEntriesPerKind = 262144;

    private readonly IPbtPersistence _inner;
    private readonly int _maxEntriesPerKind;

    private readonly ConcurrentDictionary<ValueHash256, Account?> _accounts = new();
    private readonly ConcurrentDictionary<PbtStorageTreeKey, ISlotRun> _runs = new();
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
        new InvalidatingWriteBatch(this, _inner.CreateWriteBatch(from, to, treeRoot, flags), to, clearAll: false);

    // Staging writes bypass state ids, so nothing cached can be trusted after they commit.
    public IPbtPersistence.IWriteBatch CreateStagingWriteBatch(WriteFlags flags) =>
        new InvalidatingWriteBatch(this, _inner.CreateStagingWriteBatch(flags), to: null, clearAll: true);

    public void Flush() => _inner.Flush();

    public void ClearCaches()
    {
        _inner.ClearCaches();
        using IPbtPersistence.IReader reader = _inner.CreateReader();
        OnCommitted(reader.CurrentState, writtenAccounts: null, writtenRuns: null, clearAll: true);
    }

    private bool IsCurrent(long readerGeneration) => Volatile.Read(ref _generation) == readerGeneration;

    private void TryCacheAccount(in ValueHash256 addressHash, Account? account, long readerGeneration)
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
    private void TryCacheRun(in PbtStorageTreeKey runKey, ISlotRun run, long readerGeneration)
    {
        using (_lock.EnterScope())
        {
            if (_generation != readerGeneration) return;
            if (_runs.ContainsKey(runKey)) return;
            if (_runCount >= _maxEntriesPerKind)
            {
                _runs.Clear();
                _runCount = 0;
            }
            if (_runs.TryAdd(runKey, run)) _runCount++;
        }
    }

    private void OnCommitted(StateId? to, HashSet<ValueHash256>? writtenAccounts, HashSet<PbtStorageTreeKey>? writtenRuns, bool clearAll)
    {
        using (_lock.EnterScope())
        {
            _generation++;
            if (to is { } committed) _basis = committed;
            if (clearAll)
            {
                _accounts.Clear();
                _accountCount = 0;
                _runs.Clear();
                _runCount = 0;
                return;
            }
            if (writtenAccounts is not null)
                foreach (ValueHash256 addressHash in writtenAccounts)
                    if (_accounts.TryRemove(addressHash, out _)) _accountCount--;
            if (writtenRuns is not null)
                foreach (PbtStorageTreeKey runKey in writtenRuns)
                    if (_runs.TryRemove(runKey, out _)) _runCount--;
        }
    }

    private sealed class CachingReader(PbtCarryForwardCachingPersistence parent, IPbtPersistence.IReader inner, long generation) : IPbtPersistence.IReader
    {
        public StateId CurrentState => inner.CurrentState;
        public ValueHash256 CurrentRoot => inner.CurrentRoot;

        public Account? GetAccount(in ValueHash256 addressHash)
        {
            bool current = parent.IsCurrent(generation);
            if (current && parent._accounts.TryGetValue(addressHash, out Account? cached)) return cached;
            Account? account = inner.GetAccount(addressHash);
            if (current) parent.TryCacheAccount(addressHash, account, generation);
            return account;
        }

        public ISlotRun GetSlotRun(in PbtStorageTreeKey runKey)
        {
            bool current = parent.IsCurrent(generation);
            if (current && parent._runs.TryGetValue(runKey, out ISlotRun? cached)) return cached.Clone();
            ISlotRun run = inner.GetSlotRun(runKey);
            if (current && !parent._runs.ContainsKey(runKey)) parent.TryCacheRun(runKey, run.Clone(), generation);
            return run;
        }

        public CodeInfo? GetCode(in ValueHash256 codeHash) => inner.GetCode(codeHash);
        public IPbtIterator<KeyValuePair<ValueHash256, Account>> EnumerateAccounts() => inner.EnumerateAccounts();
        public IPbtIterator<KeyValuePair<PbtStorageTreeKey, EvmWord>> EnumerateStorage(ValueHash256? addressHash = null) => inner.EnumerateStorage(addressHash);
        public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath> => inner.GetNodeGroup(groupKey);
        public IPbtIterator<PbtStorageNodePath> EnumerateNodeGroupKeys() => inner.EnumerateNodeGroupKeys();
        public void Dispose() => inner.Dispose();
    }

    private sealed class InvalidatingWriteBatch(PbtCarryForwardCachingPersistence parent, IPbtPersistence.IWriteBatch inner, StateId? to, bool clearAll) : IPbtPersistence.IWriteBatch
    {
        private HashSet<ValueHash256>? _writtenAccounts;
        private HashSet<PbtStorageTreeKey>? _writtenRuns;

        private bool _clearAll = clearAll;

        public void SetAccount(in ValueHash256 addressHash, Account? account)
        {
            (_writtenAccounts ??= []).Add(addressHash);
            inner.SetAccount(addressHash, account);
        }

        public void SetSlotRun(in PbtStorageTreeKey runKey, ISlotRun run)
        {
            (_writtenRuns ??= []).Add(runKey);
            inner.SetSlotRun(runKey, run);
        }

        public void SetCode(in ValueHash256 codeHash, CodeInfo code) => inner.SetCode(codeHash, code);

        public void ClearStorage(in ValueHash256 addressHash)
        {
            _clearAll = true;
            inner.ClearStorage(addressHash);
        }

        public void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath> => inner.SetNodeGroup(groupKey, payload);

        public void Commit()
        {
            inner.Commit();
            parent.OnCommitted(to, _writtenAccounts, _writtenRuns, _clearAll);
        }

        public void Dispose() => inner.Dispose();
    }
}
