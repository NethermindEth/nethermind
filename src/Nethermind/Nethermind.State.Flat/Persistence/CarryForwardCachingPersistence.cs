// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// <see cref="IPersistence"/> decorator that caches flat account/slot reads across heads, so a new head
/// does not re-read the serving working set from the database on its first eth_calls. Wraps the reader
/// (serve/fill) and the write batch (drops the committed write-set; clears on self-destruct or any
/// raw/range write). Generation-gated: a reader behind the cache basis bypasses it rather than serving stale data.
/// </summary>
public sealed class CarryForwardCachingPersistence : IPersistence, IAsyncDisposable
{
    private const int DefaultMaxEntriesPerKind = 262144;

    // With 4 ways, sets fill up and replace entries well before the table does. At twice the account cap, a hot working
    // set with a cold tail hits at least as often as in the 262,144-entry map this replaced, which was wiped at its cap.
    internal const int DefaultSlotCapacity = 2 * DefaultMaxEntriesPerKind;

    private readonly IPersistence _inner;
    private readonly int _maxEntriesPerKind;

    private ConcurrentDictionary<Address, Account?> _accounts;
    private readonly CarryForwardSlotTable _slots;
    private int _accountCount;
    private int _disposed;

    private readonly Lock _lock = new();
    private static long _probeBatches, _probeSetAccount, _probeEvicted, _probeClearAll, _probeHits, _probeMisses, _probeSetStorage;
    private StateId _basis;
    private long _generation;

    // Handed from one write batch to the next, so a persist does not grow fresh sets on the LOH. A spare keeps the
    // capacity of the batch that filled it for the node's lifetime, so only sets within the cache's own per-kind
    // capacity are kept.
    private HashSet<Address>? _spareWrittenAccounts;
    private HashSet<(Address, UInt256)>? _spareWrittenSlots;

    private HashSet<Address> RentWrittenAccounts() => Interlocked.Exchange(ref _spareWrittenAccounts, null) ?? [];

    private HashSet<(Address, UInt256)> RentWrittenSlots() => Interlocked.Exchange(ref _spareWrittenSlots, null) ?? [];

    private void ReturnWrittenSets(HashSet<Address>? writtenAccounts, HashSet<(Address, UInt256)>? writtenSlots)
    {
        if (writtenAccounts is not null && writtenAccounts.Count <= _maxEntriesPerKind)
        {
            writtenAccounts.Clear();
            Volatile.Write(ref _spareWrittenAccounts, writtenAccounts);
        }

        if (writtenSlots is not null && writtenSlots.Count <= _maxEntriesPerKind)
        {
            writtenSlots.Clear();
            Volatile.Write(ref _spareWrittenSlots, writtenSlots);
        }
    }

    internal bool HasSpareWrittenAccounts => Volatile.Read(ref _spareWrittenAccounts) is not null;

    internal bool HasSpareWrittenSlots => Volatile.Read(ref _spareWrittenSlots) is not null;

    /// <param name="inner">The persistence to cache reads of.</param>
    /// <param name="maxEntriesPerKind">The cached-account cap and the per-kind cap on tracked account and slot writes.</param>
    /// <param name="slotCapacity">
    /// The slot table's capacity, rounded up to a power of two of at least <see cref="CarryForwardSlotTable.Ways"/>.
    /// </param>
    public CarryForwardCachingPersistence(IPersistence inner, int maxEntriesPerKind = DefaultMaxEntriesPerKind, int slotCapacity = DefaultSlotCapacity)
    {
        _inner = inner;
        _maxEntriesPerKind = maxEntriesPerKind;
        _accounts = NewAccountCache();
        using IPersistence.IPersistenceReader reader = inner.CreateReader();
        _basis = reader.CurrentState;
        _slots = new CarryForwardSlotTable(slotCapacity);
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
        return atBasis && _slots.TryLease() ? new CachingReader(this, reader, generation) : reader;
    }

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None)
        => new InvalidatingWriteBatch(this, _inner.CreateWriteBatch(from, to, flags), to);

    public void Flush() => _inner.Flush();

    public void Clear()
    {
        using (_lock.EnterScope())
        {
            ClearAllNoLock();
        }
        _inner.Clear();
    }

    public ValueTask DisposeAsync()
    {
        // Readers still holding a lease keep the slot table alive until they are disposed. Commits and clears lease it
        // while they write it, so those readers, and any created while one is open, still see them.
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _slots.Dispose();

        return _inner is IAsyncDisposable asyncDisposable
            ? asyncDisposable.DisposeAsync()
            : ValueTask.CompletedTask;
    }

    internal CarryForwardSlotTable SlotTable => _slots;

    internal Lock CacheLock => _lock;

    // Sized for the cap up front: a ConcurrentDictionary grows by re-creating every entry, which from the default
    // capacity takes a dozen rounds on the way to the cap, the last ones tens of megabytes on whichever reader adds
    // the entry that crosses a threshold. Clear() would shrink it back to the default, so a wipe swaps in a new one.
    // One lock: every write already runs under _lock, and a dictionary grows once any one lock passes its share of
    // the buckets, so with more locks the fullest one would still trigger a growth just short of the cap.
    private ConcurrentDictionary<Address, Account?> NewAccountCache() => new(concurrencyLevel: 1, _maxEntriesPerKind);

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
                _accounts = NewAccountCache();
                _accountCount = 0;
                Metrics.IncrementCarryForwardAccountWipes();
            }
            if (_accounts.TryAdd(address, account)) _accountCount++;
            Metrics.PublishCarryForwardAccountCount(_accountCount);
        }
    }

    private void TryCacheSlot(ulong hash, Address address, in UInt256 slot, bool found, in UInt256 value, long readerGeneration)
    {
        if (_slots.TryGet(hash, address, slot, out _, out _)) return;
        // Best effort: under many threads the reads a full set loses would otherwise queue here, and a skipped insert
        // only costs a later miss.
        if (!_lock.TryEnter()) return;
        try
        {
            if (_generation != readerGeneration) return;
            switch (_slots.AddNoLock(hash, address, slot, found, value))
            {
                case CarryForwardSlotTable.AddResult.Added:
                    Metrics.PublishCarryForwardSlotCount(_slots.Count);
                    break;
                case CarryForwardSlotTable.AddResult.Replaced:
                    Metrics.IncrementCarryForwardSlotEvictions();
                    break;
            }
        }
        finally
        {
            _lock.Exit();
        }
    }

    private void OnCommitted(in StateId to, HashSet<Address>? writtenAccounts, HashSet<(Address, UInt256)>? writtenSlots, bool clearAll)
    {
        using (_lock.EnterScope())
        {
            _generation++;
            _basis = to;

            _probeBatches++;
            if (clearAll)
            {
                _probeClearAll++;
                ClearAllNoLock();
                return;
            }

            if (writtenAccounts is not null)
            {
                foreach (Address address in writtenAccounts)
                {
                    if (_accounts.TryRemove(address, out _)) { _accountCount--; _probeEvicted++; }
                }
                Metrics.PublishCarryForwardAccountCount(_accountCount);
            }

            if (writtenSlots is not null && _slots.TryLease())
            {
                try
                {
                    foreach ((Address address, UInt256 slot) in writtenSlots)
                    {
                        _slots.RemoveNoLock(CarryForwardSlotTable.Hash(address, slot), address, slot);
                    }
                }
                finally
                {
                    _slots.Dispose();
                }
                Metrics.PublishCarryForwardSlotCount(_slots.Count);
            }
        }
    }

    private void ClearAllNoLock()
    {
        // An empty cache keeps its table: raw and range writes clear everything on every batch, and snap sync and
        // healing write such a batch for every account while nothing is cached. The count is exact under _lock.
        if (_accountCount != 0) _accounts = NewAccountCache();
        _accountCount = 0;
        if (_slots.TryLease())
        {
            try
            {
                _slots.ClearNoLock();
            }
            finally
            {
                _slots.Dispose();
            }
        }
        Metrics.PublishCarryForwardAccountCount(0);
        Metrics.PublishCarryForwardSlotCount(0);
    }

    private sealed class CachingReader(CarryForwardCachingPersistence parent, IPersistence.IPersistenceReader inner, long generation)
        : IPersistence.IPersistenceReader
    {
        // A reader is shared by every thread reading its state, so enabled counters must support concurrent updates.
        // DetailedMetricsEnabled is captured once per reader after normal startup registration to avoid its hot-path
        // cost. Existing readers retain that captured value if the flag later changes.
        private readonly bool _recordDetailedMetrics = Db.Metrics.DetailedMetricsEnabled;
        private int _disposed;

        public Account? GetAccount(Address address)
        {
            bool current = parent.IsCurrent(generation);
            if (current && parent._accounts.TryGetValue(address, out Account? cached))
            {
                // Checked again after the lookup: the cache can hold an entry filled after this reader's generation ended.
                if (parent.IsCurrent(generation))
                {
                    Interlocked.Increment(ref _probeHits);
                    if (_recordDetailedMetrics) Metrics.IncrementCarryForwardAccountHits();
                    return cached;
                }

                current = false;
            }

            if (current) Interlocked.Increment(ref _probeMisses);
            if (current && _recordDetailedMetrics) Metrics.IncrementCarryForwardAccountMisses();
            Account? account = inner.GetAccount(address);
            if (current) parent.TryCacheAccount(address, account, generation);
            return account;
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
        {
            ulong hash = CarryForwardSlotTable.Hash(address, slot);
            bool current = parent.IsCurrent(generation);
            if (current && parent._slots.TryGet(hash, address, slot, out bool cachedFound, out UInt256 cachedValue))
            {
                // Checked again after the lookup: the cache can hold an entry filled after this reader's generation ended.
                if (parent.IsCurrent(generation))
                {
                    if (_recordDetailedMetrics) Metrics.IncrementCarryForwardSlotHits();
                    if (cachedFound) outValue = cachedValue;
                    return cachedFound;
                }

                current = false;
            }

            if (current && _recordDetailedMetrics) Metrics.IncrementCarryForwardSlotMisses();
            bool found = inner.TryGetSlot(address, slot, ref outValue);
            if (current) parent.TryCacheSlot(hash, address, slot, found, outValue, generation);
            return found;
        }

        public StateId CurrentState => inner.CurrentState;
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => inner.TryLoadStateRlp(path, flags);
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => inner.TryLoadStorageRlp(address, path, flags);
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => inner.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => inner.TryGetStorageRaw(addrHash, slotHash, ref value);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => inner.CreateAccountIterator(startKey, endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => inner.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
        public bool IsPreimageMode => inner.IsPreimageMode;

        /// <remarks>
        /// Reading through the reader afterwards is a caller bug, and once the persistence is disposed too it reads freed
        /// memory.
        /// </remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                inner.Dispose();
            }
            finally
            {
                parent._slots.Dispose();
            }
        }
    }

    private sealed class InvalidatingWriteBatch(CarryForwardCachingPersistence parent, IPersistence.IWriteBatch inner, StateId to)
        : IPersistence.IWriteBatch
    {
        private HashSet<Address>? _writtenAccounts;
        private HashSet<(Address, UInt256)>? _writtenSlots;
        private bool _clearAll;

        public void SelfDestruct(Address addr)
        {
            _clearAll = true;
            inner.SelfDestruct(addr);
        }

        public void SetAccount(Address addr, Account? account)
        {
            Interlocked.Increment(ref _probeSetAccount);
            if (!_clearAll) TrackWrite(_writtenAccounts ??= parent.RentWrittenAccounts(), addr);
            inner.SetAccount(addr, account);
        }

        public void SetStorage(Address addr, in UInt256 slot, in UInt256? value)
        {
            Interlocked.Increment(ref _probeSetStorage);
            if (!_clearAll) TrackWrite(_writtenSlots ??= parent.RentWrittenSlots(), (addr, slot));
            inner.SetStorage(addr, slot, value);
        }

        private void TrackWrite<TKey>(HashSet<TKey> written, TKey key)
        {
            if (written.Count < parent._maxEntriesPerKind || written.Contains(key))
            {
                written.Add(key);
                return;
            }

            _clearAll = true;
            parent.ReturnWrittenSets(_writtenAccounts, _writtenSlots);
            _writtenAccounts = null;
            _writtenSlots = null;
        }

        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue)
        {
            _clearAll = true;
            inner.SetStorageRawEncoded(addrHash, slotHash, rlpValue);
        }

        public void SetAccountRaw(in ValueHash256 addrHash, Account account)
        {
            _clearAll = true;
            inner.SetAccountRaw(addrHash, account);
        }

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            _clearAll = true;
            inner.DeleteAccountRange(fromPath, toPath);
        }

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            _clearAll = true;
            inner.DeleteStorageRange(addressHash, fromPath, toPath);
        }

        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) => inner.SetStateTrieNode(path, rlp);
        public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp) => inner.SetStorageTrieNode(address, path, rlp);
        public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to) => inner.DeleteStateTrieNodeRange(from, to);
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to) => inner.DeleteStorageTrieNodeRange(addressHash, from, to);

        public void Dispose()
        {
            inner.Dispose();
            parent.OnCommitted(to, _writtenAccounts, _writtenSlots, _clearAll);
            Console.WriteLine($"D12PROBE block={to.BlockNumber} batches={Volatile.Read(ref _probeBatches)} setAccount={Volatile.Read(ref _probeSetAccount)} setStorage={Volatile.Read(ref _probeSetStorage)} evicted={Volatile.Read(ref _probeEvicted)} clearAll={Volatile.Read(ref _probeClearAll)} hits={Volatile.Read(ref _probeHits)} misses={Volatile.Read(ref _probeMisses)} cached={parent._accountCount}");
            parent.ReturnWrittenSets(_writtenAccounts, _writtenSlots);
            _writtenAccounts = null;
            _writtenSlots = null;
        }
    }
}
