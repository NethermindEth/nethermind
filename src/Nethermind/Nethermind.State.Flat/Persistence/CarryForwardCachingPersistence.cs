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
    private const int DefaultMaxEntriesPerKind = 131072;

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

    public ValueTask DisposeAsync() => _inner is IAsyncDisposable asyncDisposable
        ? asyncDisposable.DisposeAsync()
        : ValueTask.CompletedTask;

    private bool IsCurrent(long readerGeneration) => Volatile.Read(ref _generation) == readerGeneration;

    private void TryCacheAccount(Address address, Account? account, long readerGeneration)
    {
        using (_lock.EnterScope())
        {
            if (_generation != readerGeneration) return;
            if (_accountCount >= _maxEntriesPerKind)
            {
                _accounts.Clear();
                _accountCount = 0;
            }
            if (_accounts.TryAdd(address, account)) _accountCount++;
        }
    }

    private void TryCacheSlot(in (Address, UInt256) key, in CachedSlot slot, long readerGeneration)
    {
        using (_lock.EnterScope())
        {
            if (_generation != readerGeneration) return;
            if (_slotCount >= _maxEntriesPerKind)
            {
                _slots.Clear();
                _slotCount = 0;
            }
            if (_slots.TryAdd(key, slot)) _slotCount++;
        }
    }

    private void OnCommitted(in StateId to, HashSet<Address>? writtenAccounts, HashSet<(Address, UInt256)>? writtenSlots, bool clearAll)
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
                foreach (Address address in writtenAccounts)
                {
                    if (_accounts.TryRemove(address, out _)) _accountCount--;
                }
            }

            if (writtenSlots is not null)
            {
                foreach ((Address, UInt256) key in writtenSlots)
                {
                    if (_slots.TryRemove(key, out _)) _slotCount--;
                }
            }
        }
    }

    private void ClearAllNoLock()
    {
        _accounts.Clear();
        _accountCount = 0;
        _slots.Clear();
        _slotCount = 0;
    }

    private readonly struct CachedSlot(bool found, SlotValue value)
    {
        public readonly bool Found = found;
        public readonly SlotValue Value = value;
    }

    private sealed class CachingReader(CarryForwardCachingPersistence parent, IPersistence.IPersistenceReader inner, long generation)
        : IPersistence.IPersistenceReader
    {
        public Account? GetAccount(Address address)
        {
            bool current = parent.IsCurrent(generation);
            if (current && parent._accounts.TryGetValue(address, out Account? cached)) return cached;

            Account? account = inner.GetAccount(address);
            if (current) parent.TryCacheAccount(address, account, generation);
            return account;
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
        {
            (Address, UInt256) key = (address, slot);
            bool current = parent.IsCurrent(generation);
            if (current && parent._slots.TryGetValue(key, out CachedSlot cached))
            {
                if (cached.Found) outValue = cached.Value;
                return cached.Found;
            }

            bool found = inner.TryGetSlot(address, slot, ref outValue);
            if (current) parent.TryCacheSlot(key, new CachedSlot(found, found ? outValue : default), generation);
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
            (_writtenAccounts ??= []).Add(addr);
            inner.SetAccount(addr, account);
        }

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value)
        {
            (_writtenSlots ??= []).Add((addr, slot));
            inner.SetStorage(addr, slot, value);
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
        }
    }
}
