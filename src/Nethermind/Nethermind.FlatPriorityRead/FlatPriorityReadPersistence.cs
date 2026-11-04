// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.FlatPriorityRead;

/// <summary>
/// <see cref="IPersistence"/> decorator that answers flat slot reads of the configured contracts from an
/// <see cref="IPrioritySlotStore"/>, so they never reach the inner persistence.
/// </summary>
/// <remarks>
/// <para>
/// The store mirrors the inner persistence for those contracts, absent slots included. The constructor loads it from
/// the inner persistence, blocking until done, and every write batch is mirrored into it. A reader pairs an inner
/// reader with a store snapshot of the same <see cref="StateId"/>.
/// </para>
/// <para>
/// Range deletes and <see cref="Clear"/> are mirrored by reading the doomed slots from the inner persistence as it
/// was before the batch, so the store only needs point writes. A batch buffers its mirrored changes and commits them
/// right after the inner commit under one lock, so the store commits in the inner persistence's order even when sync
/// writes several batches at once.
/// </para>
/// </remarks>
public sealed class FlatPriorityReadPersistence : IPersistence
{
    private const int LoadCommitInterval = 1 << 20;
    private static readonly TimeSpan ReaderMatchTimeout = TimeSpan.FromSeconds(30);

    private readonly IPersistence _inner;
    private readonly IPrioritySlotStore _store;
    private readonly bool _isPreimageMode;
    private readonly ValueHash256[] _addressKeys;
    private readonly FrozenDictionary<Address, byte> _contractByAddress;
    private readonly FrozenDictionary<ValueHash256, byte> _contractByAddressKey;
    private readonly Lock _commitLock = new();

    public FlatPriorityReadPersistence(IPersistence inner, IPrioritySlotStore store, IFlatPriorityReadConfig config, ILogManager logManager)
    {
        _inner = inner;
        _store = store;
        Address[] addresses = ParseAddresses(config.Addresses);

        using IPersistence.IPersistenceReader reader = inner.CreateReader();
        _isPreimageMode = reader.IsPreimageMode;
        _addressKeys = new ValueHash256[addresses.Length];
        Dictionary<Address, byte> contractByAddress = [];
        Dictionary<ValueHash256, byte> contractByAddressKey = [];
        for (int contract = 0; contract < addresses.Length; contract++)
        {
            _addressKeys[contract] = AddressKey(addresses[contract]);
            contractByAddress[addresses[contract]] = (byte)contract;
            contractByAddressKey[_addressKeys[contract]] = (byte)contract;
        }

        _contractByAddress = contractByAddress.ToFrozenDictionary();
        _contractByAddressKey = contractByAddressKey.ToFrozenDictionary();

        Load(reader, logManager.GetClassLogger<FlatPriorityReadPersistence>());
    }

    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        long started = Stopwatch.GetTimestamp();
        SpinWait spin = default;
        while (true)
        {
            IPersistence.IPersistenceReader inner = _inner.CreateReader(flags);
            IPrioritySlotStore.IReader store = _store.BeginRead();
            if (store.State == inner.CurrentState) return new PriorityReader(this, inner, store);

            // The two only differ between an inner commit and the store commit that follows it.
            StateId innerState = inner.CurrentState;
            StateId storeState = store.State;
            store.Dispose();
            inner.Dispose();
            if (Stopwatch.GetElapsedTime(started) > ReaderMatchTimeout)
            {
                throw new InvalidOperationException($"The priority slot store stayed at {storeState} while the flat persistence is at {innerState}");
            }

            spin.SpinOnce();
        }
    }

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) =>
        new MirroringWriteBatch(this, _inner.CreateWriteBatch(from, to, flags), to);

    public void Flush() => _inner.Flush();

    public void Clear()
    {
        using Lock.Scope _ = _commitLock.EnterScope();
        List<SlotChange> changes = [];
        using (IPersistence.IPersistenceReader reader = _inner.CreateReader())
        {
            for (int contract = 0; contract < _addressKeys.Length; contract++)
            {
                CollectRemovals(reader, (byte)contract, ValueKeccak.Zero, ValueKeccak.MaxValue, changes);
            }
        }

        _inner.Clear();
        using IPersistence.IPersistenceReader cleared = _inner.CreateReader();
        CommitToStore(changes, cleared.CurrentState);
    }

    private static Address[] ParseAddresses(string? addresses)
    {
        const string setting = $"FlatPriorityRead.{nameof(IFlatPriorityReadConfig.Addresses)}";
        string[] entries = (addresses ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length is 0 or > PrioritySlotEncoding.MaxContracts)
        {
            throw new InvalidConfigurationException($"{setting} must list 1 to {PrioritySlotEncoding.MaxContracts} addresses, not {entries.Length}.", ExitCodes.GeneralError);
        }

        Address[] parsed = new Address[entries.Length];
        HashSet<Address> seen = [];
        for (int i = 0; i < entries.Length; i++)
        {
            if (!Address.TryParse(entries[i], out Address? address) || address is null || !seen.Add(address))
            {
                throw new InvalidConfigurationException($"{setting} has an invalid or repeated address: {entries[i]}.", ExitCodes.GeneralError);
            }

            parsed[i] = address;
        }

        return parsed;
    }

    private ValueHash256 AddressKey(Address address)
    {
        if (!_isPreimageMode) return address.ToAccountPath;

        ValueHash256 key = default;
        address.Bytes.CopyTo(key.BytesAsSpan);
        return key;
    }

    private ValueHash256 SlotKey(in UInt256 slot)
    {
        ValueHash256 key = default;
        if (_isPreimageMode) slot.ToBigEndian(key.BytesAsSpan);
        else StorageTree.ComputeKeyWithLookup(slot, ref key);
        return key;
    }

    private void Load(IPersistence.IPersistenceReader reader, ILogger logger)
    {
        long started = Stopwatch.GetTimestamp();
        long loaded = 0;
        IPrioritySlotStore.IWriter writer = _store.BeginWrite();
        try
        {
            for (int contract = 0; contract < _addressKeys.Length; contract++)
            {
                using IPersistence.IFlatIterator slots = reader.CreateStorageIterator(_addressKeys[contract], ValueKeccak.Zero, ValueKeccak.MaxValue);
                while (slots.MoveNext())
                {
                    // The iterator exposes only the stored encoding, which this read decodes.
                    ValueHash256 slotKey = slots.CurrentKey;
                    UInt256 value = default;
                    if (!reader.TryGetStorageRaw(_addressKeys[contract], slotKey, ref value)) continue;

                    writer.Set((byte)contract, slotKey, value);

                    // Nothing reads the store before the load ends, so committing part of it bounds the writer's memory.
                    if (++loaded % LoadCommitInterval == 0)
                    {
                        writer.Commit(reader.CurrentState);
                        writer.Dispose();
                        writer = _store.BeginWrite();
                    }
                }
            }

            writer.Commit(reader.CurrentState);
        }
        finally
        {
            writer.Dispose();
        }

        // Opened before any batch can exist: a store may treat its first reader as the end of the load.
        using IPrioritySlotStore.IReader loadedStore = _store.BeginRead();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        Metrics.FlatPriorityReadLoadedSlots = loaded;
        Metrics.FlatPriorityReadLoadMilliseconds = (long)elapsed.TotalMilliseconds;
        if (logger.IsInfo) logger.Info($"Loaded {loaded:N0} slots of {_addressKeys.Length} priority contracts in {elapsed.TotalSeconds:N1}s at state {loadedStore.State}");
    }

    private void CollectRemovals(IPersistence.IPersistenceReader reader, byte contract, in ValueHash256 from, in ValueHash256 to, List<SlotChange> changes)
    {
        using IPersistence.IFlatIterator slots = reader.CreateStorageIterator(_addressKeys[contract], from, to);
        while (slots.MoveNext()) changes.Add(new SlotChange(contract, slots.CurrentKey, null));
    }

    private void CommitToStore(List<SlotChange>? changes, in StateId state)
    {
        using IPrioritySlotStore.IWriter writer = _store.BeginWrite();
        if (changes is not null)
        {
            foreach (SlotChange change in changes)
            {
                if (change.Value is { } value) writer.Set(change.Contract, change.SlotKey, value);
                else writer.Remove(change.Contract, change.SlotKey);
            }
        }

        writer.Commit(state);
    }

    private readonly record struct SlotChange(byte Contract, ValueHash256 SlotKey, UInt256? Value);

    private sealed class PriorityReader(FlatPriorityReadPersistence parent, IPersistence.IPersistenceReader inner, IPrioritySlotStore.IReader store)
        : IPersistence.IPersistenceReader, IPersistence.IPrefetchingReader
    {
        // Captured once per reader, as in CarryForwardCachingPersistence, to keep the flag off the hot path.
        private readonly bool _recordDetailedMetrics = Db.Metrics.DetailedMetricsEnabled;
        private int _disposed;

        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
        {
            if (!parent._contractByAddress.TryGetValue(address, out byte contract)) return inner.TryGetSlot(address, slot, ref outValue);

            bool found = store.TryGet(contract, parent.SlotKey(slot), out UInt256 value);
            if (found) outValue = value;
            if (_recordDetailedMetrics)
            {
                if (found) Metrics.IncrementFound();
                else Metrics.IncrementNotFound();
            }

            return found;
        }

        public Account? GetAccount(Address address) => inner.GetAccount(address);
        public StateId CurrentState => inner.CurrentState;
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => inner.TryLoadStateRlp(path, flags);
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => inner.TryLoadStorageRlp(address, path, flags);
        public bool TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct => inner.TryLoadStateRlp(path, flags, action, arg);
        public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct => inner.TryLoadStorageRlp(address, path, flags, action, arg);
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => inner.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => inner.TryGetStorageRaw(addrHash, slotHash, ref value);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => inner.CreateAccountIterator(startKey, endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => inner.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
        public bool IsPreimageMode => inner.IsPreimageMode;

        public bool SupportsPrefetch => inner is IPersistence.IPrefetchingReader { SupportsPrefetch: true };

        /// <summary>Plans on the slot samples the store does not answer.</summary>
        public IPersistence.PrefetchColumns PlanPrefetch(Span<Address> accountSamples, Span<StorageCell> slotSamples) =>
            inner is IPersistence.IPrefetchingReader prefetcher
                ? prefetcher.PlanPrefetch(accountSamples, DropPriority(slotSamples))
                : IPersistence.PrefetchColumns.None;

        /// <summary>Forwards only the slots the store does not answer.</summary>
        public void Prefetch(IPersistence.PrefetchColumns columns, Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken)
        {
            if (inner is IPersistence.IPrefetchingReader prefetcher) prefetcher.Prefetch(columns, accounts, DropPriority(slots), cancellationToken);
        }

        // Compacted in place: the span is the caller's scratch.
        private Span<StorageCell> DropPriority(Span<StorageCell> slots)
        {
            int kept = 0;
            foreach (StorageCell cell in slots)
            {
                if (!parent._contractByAddress.ContainsKey(cell.Address)) slots[kept++] = cell;
            }

            return slots[..kept];
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            store.Dispose();
            inner.Dispose();
        }
    }

    private sealed class MirroringWriteBatch(FlatPriorityReadPersistence parent, IPersistence.IWriteBatch inner, StateId to) : IPersistence.IWriteBatch
    {
        private List<SlotChange>? _changes;
        private IPersistence.IPersistenceReader? _preBatchReader;

        private List<SlotChange> Changes => _changes ??= [];

        public void SetStorage(Address addr, in UInt256 slot, in UInt256? value)
        {
            if (parent._contractByAddress.TryGetValue(addr, out byte contract)) Changes.Add(new SlotChange(contract, parent.SlotKey(slot), value));
            inner.SetStorage(addr, slot, value);
        }

        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue)
        {
            if (parent._contractByAddressKey.TryGetValue(addrHash, out byte contract))
            {
                RlpReader rlp = new(rlpValue);
                Changes.Add(new SlotChange(contract, slotHash, new UInt256(rlp.DecodeByteArraySpan(), isBigEndian: true)));
            }

            inner.SetStorageRawEncoded(addrHash, slotHash, rlpValue);
        }

        public void SelfDestruct(Address addr)
        {
            if (parent._contractByAddress.TryGetValue(addr, out byte contract)) RemovePreBatchSlots(contract, ValueKeccak.Zero, ValueKeccak.MaxValue);
            inner.SelfDestruct(addr);
        }

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            if (parent._contractByAddressKey.TryGetValue(addressHash, out byte contract)) RemovePreBatchSlots(contract, fromPath, toPath);
            inner.DeleteStorageRange(addressHash, fromPath, toPath);
        }

        public void SelfDestructRange(in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            // Storage is keyed by the first 20 bytes of the account path, so the inner persistence compares only those.
            ReadOnlySpan<byte> lowest = fromPath.Bytes[..Address.Size];
            ReadOnlySpan<byte> highest = toPath.Bytes[..Address.Size];
            for (int contract = 0; contract < parent._addressKeys.Length; contract++)
            {
                ReadOnlySpan<byte> addressKey = parent._addressKeys[contract].Bytes[..Address.Size];
                if (addressKey.SequenceCompareTo(lowest) >= 0 && addressKey.SequenceCompareTo(highest) <= 0)
                {
                    RemovePreBatchSlots((byte)contract, ValueKeccak.Zero, ValueKeccak.MaxValue);
                }
            }

            inner.SelfDestructRange(fromPath, toPath);
        }

        public void SetAccount(Address addr, Account? account) => inner.SetAccount(addr, account);
        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) => inner.SetStateTrieNode(path, rlp);
        public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp) => inner.SetStorageTrieNode(address, path, rlp);
        public void SetAccountRaw(in ValueHash256 addrHash, Account account) => inner.SetAccountRaw(addrHash, account);
        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) => inner.DeleteAccountRange(fromPath, toPath);
        public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to) => inner.DeleteStateTrieNodeRange(from, to);
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to) => inner.DeleteStorageTrieNodeRange(addressHash, from, to);

        public void Dispose()
        {
            _preBatchReader?.Dispose();
            using Lock.Scope _ = parent._commitLock.EnterScope();
            inner.Dispose();
            parent.CommitToStore(_changes, to);
        }

        // Wipes remove what the inner persistence held before this batch, as its own wipes do.
        private void RemovePreBatchSlots(byte contract, in ValueHash256 from, in ValueHash256 to) =>
            parent.CollectRemovals(_preBatchReader ??= parent._inner.CreateReader(), contract, from, to, Changes);
    }
}
