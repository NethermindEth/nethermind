// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
/// the account-count gauge and the shared account/slot wipe counter form a sawtooth under sustained churn.
/// A rising wipe rate is the signal that the cap is binding and a warm working set is being discarded.
/// </remarks>
public sealed class CarryForwardCachingPersistence : IPersistence, IAsyncDisposable
{
    private const int DefaultMaxEntriesPerKind = 262144;
    // Avoid repeated early table growth while keeping the default construction cost below the full cap.
    private const int InitialEntriesPerKind = DefaultMaxEntriesPerKind / 8;

    private readonly IPersistence _inner;
    private readonly int _maxEntriesPerKind;

    private readonly ConcurrentDictionary<Address, CachedAccount> _accounts;
    private readonly ConcurrentDictionary<(Address, UInt256), CachedSlot> _slots;
    private int _accountCount;
    private int _slotCount;

    private readonly Lock _lock = new();
    private StateId _basis;
    private long _generation;

    public CarryForwardCachingPersistence(IPersistence inner, int maxEntriesPerKind = DefaultMaxEntriesPerKind)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEntriesPerKind);
        _inner = inner;
        _maxEntriesPerKind = maxEntriesPerKind;
        int initialCapacity = Math.Min(maxEntriesPerKind, InitialEntriesPerKind);
        _accounts = new(Environment.ProcessorCount, initialCapacity);
        _slots = new(Environment.ProcessorCount, initialCapacity);
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
            if (_accounts.TryAdd(address, new CachedAccount(account, readerGeneration))) _accountCount++;
            Metrics.PublishCarryForwardAccountCount(_accountCount);
        }
    }

    private void TryCacheSlot(in (Address, UInt256) key, bool found, in UInt256 value, long readerGeneration)
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
            if (_slots.TryAdd(key, new CachedSlot(found, value, readerGeneration))) _slotCount++;
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
                    CachedAccount refreshed = new(written.Value, _generation);
                    if (_accountCount < _maxEntriesPerKind)
                    {
                        if (_accounts.TryAdd(written.Key, refreshed)) _accountCount++;
                        else _accounts[written.Key] = refreshed;
                    }
                    else if (_accounts.ContainsKey(written.Key))
                    {
                        _accounts[written.Key] = refreshed;
                    }
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

    private sealed class CachedAccount(Account? value, long generation)
    {
        public readonly Account? Value = value;
        public readonly long Generation = generation;
    }

    private readonly struct CachedSlot(bool found, UInt256 value, long generation)
    {
        public readonly bool Found = found;
        public readonly UInt256 Value = value;
        public readonly long Generation = generation;
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

            if (parent._accounts.TryGetValue(address, out CachedAccount? cachedAccount)
                && cachedAccount!.Generation <= generation)
            {
                if (_recordDetailedMetrics) Metrics.IncrementCarryForwardAccountHits();
                return cachedAccount.Value;
            }

            if (_recordDetailedMetrics) Metrics.IncrementCarryForwardAccountMisses();
            Account? account = inner.GetAccount(address);
            parent.TryCacheAccount(address, account, generation);
            return account;
        }

        public void GetAccounts(ReadOnlySpan<Address> addresses, Span<Account?> accounts)
        {
            if (addresses.Length != accounts.Length)
                throw new ArgumentException("Addresses and accounts must have the same length.", nameof(accounts));

            bool current = parent.IsCurrent(generation);
            if (!current)
            {
                inner.GetAccounts(addresses, accounts);
                return;
            }

            using ArrayPoolListRef<Address> missingAddresses = new(addresses.Length);
            using ArrayPoolListRef<int> missingIndices = new(addresses.Length);
            int missingCount = 0;
            for (int i = 0; i < addresses.Length; i++)
            {
                Address address = addresses[i];
                if (parent._accounts.TryGetValue(address, out CachedAccount? cachedAccount)
                    && cachedAccount!.Generation <= generation)
                {
                    if (_recordDetailedMetrics) Metrics.IncrementCarryForwardAccountHits();
                    accounts[i] = cachedAccount.Value;
                    continue;
                }

                if (_recordDetailedMetrics) Metrics.IncrementCarryForwardAccountMisses();
                missingAddresses.Add(address);
                missingIndices.Add(i);
                missingCount++;
            }

            if (missingCount == 0)
            {
                if (!parent.IsCurrent(generation))
                    inner.GetAccounts(addresses, accounts);
                return;
            }

            using ArrayPoolListRef<Account?> missingAccounts = new(missingCount, missingCount);
            inner.GetAccounts(missingAddresses.AsSpan(), missingAccounts.AsSpan());
            if (!parent.IsCurrent(generation))
            {
                inner.GetAccounts(addresses, accounts);
                return;
            }

            for (int i = 0; i < missingCount; i++)
            {
                Address address = missingAddresses[i];
                Account? account = missingAccounts[i];
                accounts[missingIndices[i]] = account;
                parent.TryCacheAccount(address, account, generation);
            }
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
        {
            (Address, UInt256) key = (address, slot);
            bool current = parent.IsCurrent(generation);
            if (!current)
                return inner.TryGetSlot(address, slot, ref outValue);

            if (parent._slots.TryGetValue(key, out CachedSlot cachedSlot) && cachedSlot.Generation <= generation)
            {
                if (_recordDetailedMetrics) Metrics.IncrementCarryForwardSlotHits();
                if (cachedSlot.Found) outValue = cachedSlot.Value;
                return cachedSlot.Found;
            }

            if (_recordDetailedMetrics) Metrics.IncrementCarryForwardSlotMisses();
            bool found = inner.TryGetSlot(address, slot, ref outValue);
            parent.TryCacheSlot(key, found, found ? outValue : default, generation);
            return found;
        }

        public void GetSlots(ReadOnlySpan<StorageCell> storageCells, Span<UInt256> slots, Span<bool> found)
        {
            if (storageCells.Length != slots.Length || storageCells.Length != found.Length)
                throw new ArgumentException("Storage cells, slots, and found flags must have the same length.", nameof(slots));

            bool current = parent.IsCurrent(generation);
            if (!current)
            {
                GetSlotsFromInner(storageCells, slots, found);
                return;
            }

            using ArrayPoolListRef<StorageCell> missingCells = new(storageCells.Length);
            using ArrayPoolListRef<int> missingIndices = new(storageCells.Length);
            int missingCount = 0;
            for (int i = 0; i < storageCells.Length; i++)
            {
                StorageCell cell = storageCells[i];
                if (parent._slots.TryGetValue((cell.Address, cell.Index), out CachedSlot cachedSlot)
                    && cachedSlot.Generation <= generation)
                {
                    if (_recordDetailedMetrics) Metrics.IncrementCarryForwardSlotHits();
                    found[i] = cachedSlot.Found;
                    slots[i] = cachedSlot.Found ? cachedSlot.Value : default;
                    continue;
                }

                if (_recordDetailedMetrics) Metrics.IncrementCarryForwardSlotMisses();
                missingCells.Add(cell);
                missingIndices.Add(i);
                missingCount++;
            }

            if (missingCount == 0)
            {
                if (!parent.IsCurrent(generation))
                    GetSlotsFromInner(storageCells, slots, found);
                return;
            }

            using ArrayPoolListRef<UInt256> missingSlots = new(missingCount, missingCount);
            using ArrayPoolListRef<bool> missingFound = new(missingCount, missingCount);
            inner.GetSlots(missingCells.AsSpan(), missingSlots.AsSpan(), missingFound.AsSpan());
            if (!parent.IsCurrent(generation))
            {
                GetSlotsFromInner(storageCells, slots, found);
                return;
            }

            for (int i = 0; i < missingCount; i++)
            {
                StorageCell cell = missingCells[i];
                UInt256 slot = missingSlots[i];
                bool slotFound = missingFound[i];
                int destinationIndex = missingIndices[i];
                slots[destinationIndex] = slotFound ? slot : default;
                found[destinationIndex] = slotFound;
                parent.TryCacheSlot((cell.Address, cell.Index), slotFound, slotFound ? slot : default, generation);
            }
        }

        private void GetSlotsFromInner(ReadOnlySpan<StorageCell> storageCells, Span<UInt256> slots, Span<bool> found)
        {
            inner.GetSlots(storageCells, slots, found);
            for (int i = 0; i < found.Length; i++)
                if (!found[i]) slots[i] = default;
        }

        public StateId CurrentState => inner.CurrentState;
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => inner.TryLoadStateRlp(path, flags);
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => inner.TryLoadStorageRlp(address, path, flags);
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => inner.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => inner.TryGetStorageRaw(addrHash, slotHash, ref value);
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
            // These operations are also used outside PersistenceManager's abort wrapper; a failed inner
            // operation must not let Dispose publish a partial batch target as the new cache basis.
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

        public void SetStorage(Address addr, in UInt256 slot, in UInt256? value)
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
                _abandoned = true;
                throw;
            }
            finally
            {
                // After the inner commit, matching the commit path's ordering: a reader created between the
                // abort and the commit still matches the basis, so clearing earlier would let its fills install
                // pre-commit values that no later write-set refresh corrects.
                if (_abandoned) parent.Abort();
            }

            if (!_abandoned)
                parent.OnCommitted(to, _writtenAccounts, _writtenSlots, _clearAll);
        }

        void IAbortableWriteBatch.Abandon() => Abandon();

        // Flag only; parent.Abort() runs in Dispose once the inner batch has committed or thrown.
        private void Abandon() => _abandoned = true;
    }
}
