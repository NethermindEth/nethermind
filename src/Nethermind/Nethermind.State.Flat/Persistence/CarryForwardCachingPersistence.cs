// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

internal interface IAbortableWriteBatch
{
    void Abandon();
}

/// <summary>
/// <see cref="IPersistence"/> decorator that caches flat account/slot reads across heads, so a new head
/// does not re-read the serving working set from the database on its first eth_calls. Wraps the reader
/// (serve/fill) and the write batch: committed accounts are refreshed or admitted when capacity permits,
/// committed slots are invalidated, and self-destruct or raw account/slot and range writes clear the cache.
/// Generation-gated: a reader behind the cache basis bypasses it rather than serving stale data.
/// </summary>
/// <remarks>
/// There is no per-entry account eviction: residency grows until the entry cap forces a wholesale wipe, so
/// the account-count gauge and the account wipe counter form a sawtooth under sustained churn. A rising
/// wipe rate is the signal that the cap is binding and a warm working set is being discarded.
/// </remarks>
public sealed class CarryForwardCachingPersistence : IPersistence, IAsyncDisposable
{
    private const int DefaultMaxEntriesPerKind = 262144;

    private readonly IPersistence _inner;
    private readonly int _maxEntriesPerKind;

    private readonly ConcurrentDictionary<Address, Account?> _accounts = new();
    private readonly ConcurrentDictionary<(Address, UInt256), CachedSlot> _slots = new();
    private int _accountCount;
    private int _slotCount;

    private readonly Lock _lock = new();
    private StateId _basis;
    private long _generation;

    public CarryForwardCachingPersistence(IPersistence inner, int maxEntriesPerKind = DefaultMaxEntriesPerKind)
    {
        _inner = inner;
        _maxEntriesPerKind = maxEntriesPerKind;
        using IPersistence.IPersistenceReader reader = inner.CreateReader();
        _basis = reader.CurrentState;
    }

    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        IPersistence.IPersistenceReader reader = _inner.CreateReader(flags);
        if ((flags & ReaderFlags.Sync) != 0) return reader;

        long generation;
        bool atBasis;
        using (_lock.EnterScope())
        {
            atBasis = reader.CurrentState == _basis;
            generation = _generation;
        }
        return atBasis ? new CachingReader(this, reader, generation) : reader;
    }

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None)
        => new CacheUpdatingWriteBatch(this, _inner.CreateWriteBatch(from, to, flags), to);

    public void Flush() => _inner.Flush();

    public void Clear()
    {
        using (_lock.EnterScope())
        {
            ClearAllNoLock();
        }
        _inner.Clear();
    }

    public ValueTask DisposeAsync() => _inner is IAsyncDisposable asyncDisposable
        ? asyncDisposable.DisposeAsync()
        : ValueTask.CompletedTask;

    private bool IsCurrent(long readerGeneration) => Volatile.Read(ref _generation) == readerGeneration;

    private void TryCacheAccount(Address address, Account? account, long readerGeneration)
    {
        // Another reader can fill the same base miss while this reader is doing I/O.
        // The cache is best-effort, so a racing removal only loses a hint.
        if (_accounts.ContainsKey(address)) return;
        using (_lock.EnterScope())
        {
            if (_generation != readerGeneration) return;
            if (_accounts.ContainsKey(address)) return;
            if (_accountCount >= _maxEntriesPerKind)
            {
                _accounts.Clear();
                _accountCount = 0;
                Metrics.IncrementCarryForwardWipes();
            }
            if (_accounts.TryAdd(address, account)) _accountCount++;
            Metrics.PublishCarryForwardAccountCount(_accountCount);
        }
    }

    private void TryCacheSlot(in (Address, UInt256) key, in CachedSlot slot, long readerGeneration)
    {
        if (_slots.ContainsKey(key)) return;
        using (_lock.EnterScope())
        {
            if (_generation != readerGeneration) return;
            if (_slots.ContainsKey(key)) return;
            if (_slotCount >= _maxEntriesPerKind)
            {
                _slots.Clear();
                _slotCount = 0;
                Metrics.IncrementCarryForwardWipes();
            }
            if (_slots.TryAdd(key, slot)) _slotCount++;
            Metrics.PublishCarryForwardSlotCount(_slotCount);
        }
    }

    private void OnCommitted(in StateId to, Dictionary<Address, Account?>? writtenAccounts, HashSet<(Address, UInt256)>? writtenSlots, bool clearAll)
    {
        using (_lock.EnterScope())
        {
            _generation++;
            _basis = to;

            if (clearAll)
            {
                ClearAllNoLock();
                return;
            }

            if (writtenAccounts is not null)
            {
                // Refresh rather than evict. Nearly every transaction writes its sender's nonce and
                // balance, so evicting the write-set kept the account cache pinned near empty and
                // discarded exactly the entries most likely to be read again. The committed value is
                // the new state, so caching it is as correct as caching a read of it.
                foreach (KeyValuePair<Address, Account?> written in writtenAccounts)
                {
                    if (_accounts.ContainsKey(written.Key)) _accounts[written.Key] = written.Value;
                    else if (_accountCount < _maxEntriesPerKind && _accounts.TryAdd(written.Key, written.Value)) _accountCount++;
                }
                Metrics.PublishCarryForwardAccountCount(_accountCount);
            }

            if (writtenSlots is not null)
            {
                foreach ((Address, UInt256) key in writtenSlots)
                {
                    if (_slots.TryRemove(key, out _)) _slotCount--;
                }
                Metrics.PublishCarryForwardSlotCount(_slotCount);
            }
        }
    }

    private void ClearAllNoLock()
    {
        _accounts.Clear();
        _accountCount = 0;
        _slots.Clear();
        _slotCount = 0;
        Metrics.PublishCarryForwardAccountCount(0);
        Metrics.PublishCarryForwardSlotCount(0);
    }

    private void Abort()
    {
        // Do not call IPersistence.Clear here: it clears the database, not just this decorator's cache.
        using (_lock.EnterScope())
        {
            _generation++;
            ClearAllNoLock();
        }
    }

    private readonly struct CachedSlot(bool found, SlotValue value)
    {
        public readonly bool Found = found;
        public readonly SlotValue Value = value;
    }

    private sealed class CachingReader(CarryForwardCachingPersistence parent, IPersistence.IPersistenceReader inner, long generation)
        : IPersistence.IPersistenceReader
    {
        // A reader is shared by every thread reading its state, so enabled counters must support concurrent updates.
        // DetailedMetricsEnabled is captured once per reader after normal startup registration to avoid its hot-path
        // cost. Existing readers retain that captured value if the flag later changes.
        private readonly bool _recordDetailedMetrics = Db.Metrics.DetailedMetricsEnabled;

        public Account? GetAccount(Address address)
        {
            bool current = parent.IsCurrent(generation);
            if (!current)
                return inner.GetAccount(address);

            if (parent._accounts.TryGetValue(address, out Account? account))
            {
                if (_recordDetailedMetrics) Metrics.IncrementCarryForwardAccountHits();
                return account;
            }

            if (_recordDetailedMetrics) Metrics.IncrementCarryForwardAccountMisses();
            account = inner.GetAccount(address);
            parent.TryCacheAccount(address, account, generation);
            return account;
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
        {
            (Address, UInt256) key = (address, slot);
            bool current = parent.IsCurrent(generation);
            if (!current)
                return inner.TryGetSlot(address, slot, ref outValue);

            if (parent._slots.TryGetValue(key, out CachedSlot cachedSlot))
            {
                if (_recordDetailedMetrics) Metrics.IncrementCarryForwardSlotHits();
                if (cachedSlot.Found) outValue = cachedSlot.Value;
                return cachedSlot.Found;
            }

            if (_recordDetailedMetrics) Metrics.IncrementCarryForwardSlotMisses();
            bool found = inner.TryGetSlot(address, slot, ref outValue);
            parent.TryCacheSlot(key, new CachedSlot(found, found ? outValue : default), generation);
            return found;
        }

        public StateId CurrentState => inner.CurrentState;
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => inner.TryLoadStateRlp(path, flags);
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => inner.TryLoadStorageRlp(address, path, flags);
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => inner.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) => inner.TryGetStorageRaw(addrHash, slotHash, ref value);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => inner.CreateAccountIterator(startKey, endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => inner.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
        public bool IsPreimageMode => inner.IsPreimageMode;
        public void Dispose() => inner.Dispose();
    }

    private sealed class CacheUpdatingWriteBatch(CarryForwardCachingPersistence parent, IPersistence.IWriteBatch inner, StateId to)
        : IPersistence.IWriteBatch, IAbortableWriteBatch
    {
        private Dictionary<Address, Account?>? _writtenAccounts;
        private HashSet<(Address, UInt256)>? _writtenSlots;
        private bool _clearAll;
        private bool _abandoned;

        public void SelfDestruct(Address addr)
        {
            try
            {
                inner.SelfDestruct(addr);
                _clearAll = true;
            }
            catch
            {
                Abandon();
                throw;
            }
        }

        public void SetAccount(Address addr, Account? account)
        {
            try
            {
                inner.SetAccount(addr, account);
                (_writtenAccounts ??= [])[addr] = account;
            }
            catch
            {
                Abandon();
                throw;
            }
        }

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value)
        {
            try
            {
                inner.SetStorage(addr, slot, value);
                (_writtenSlots ??= []).Add((addr, slot));
            }
            catch
            {
                Abandon();
                throw;
            }
        }

        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue)
        {
            try
            {
                inner.SetStorageRawEncoded(addrHash, slotHash, rlpValue);
                _clearAll = true;
            }
            catch
            {
                Abandon();
                throw;
            }
        }

        public void SetAccountRaw(in ValueHash256 addrHash, Account account)
        {
            try
            {
                inner.SetAccountRaw(addrHash, account);
                _clearAll = true;
            }
            catch
            {
                Abandon();
                throw;
            }
        }

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            try
            {
                inner.DeleteAccountRange(fromPath, toPath);
                _clearAll = true;
            }
            catch
            {
                Abandon();
                throw;
            }
        }

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            try
            {
                inner.DeleteStorageRange(addressHash, fromPath, toPath);
                _clearAll = true;
            }
            catch
            {
                Abandon();
                throw;
            }
        }

        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp)
        {
            try
            {
                inner.SetStateTrieNode(path, rlp);
            }
            catch
            {
                Abandon();
                throw;
            }
        }

        public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp)
        {
            try
            {
                inner.SetStorageTrieNode(address, path, rlp);
            }
            catch
            {
                Abandon();
                throw;
            }
        }

        public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to)
        {
            try
            {
                inner.DeleteStateTrieNodeRange(from, to);
            }
            catch
            {
                Abandon();
                throw;
            }
        }

        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to)
        {
            try
            {
                inner.DeleteStorageTrieNodeRange(addressHash, from, to);
            }
            catch
            {
                Abandon();
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                inner.Dispose();
            }
            catch
            {
                Abandon();
                throw;
            }

            if (!_abandoned)
                parent.OnCommitted(to, _writtenAccounts, _writtenSlots, _clearAll);
        }

        void IAbortableWriteBatch.Abandon() => Abandon();

        private void Abandon()
        {
            if (_abandoned) return;
            _abandoned = true;
            parent.Abort();
        }
    }
}
