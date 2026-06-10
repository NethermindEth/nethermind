// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

public class RefCountingPersistenceReader : RefCountingDisposable, IPersistence.IPersistenceReader
{
    private const int NoAccessors = 0; // Same as parent's constant
    private const int Disposing = -1; // Same as parent's constant
    private readonly IPersistence.IPersistenceReader _innerReader;
    // Read-through hot-slot value cache, shared across reader recreations and owned by
    // CachedReaderPersistence (which clears it on write). Null = caching disabled.
    private readonly AssociativeCache<StorageCell, CachedSlot>? _storageCache;
    private CancellationTokenSource? _cts = new();
    public RefCountingPersistenceReader(IPersistence.IPersistenceReader innerReader, ILogger logger, AssociativeCache<StorageCell, CachedSlot>? storageCache = null)
    {
        _innerReader = innerReader;
        _storageCache = storageCache;

        _ = Task.Run(async () =>
        {
            // Reader should be re-created every block unless something holds it for very long.
            // It prevents database compaction, so this needs to be closed eventually.
            while (true)
            {
                if (!await Nethermind.Core.Extensions.TaskExtensions.DelaySafe(60_000, _cts?.Token ?? CancellationToken.None)) return;
                if (Volatile.Read(ref _leases.Value) <= NoAccessors) return;
                if (logger.IsWarn)
                    logger.Warn($"Unexpected old snapshot created. Lease count {_leases.Value}. State {CurrentState}");
            }
        });
    }

    public Account? GetAccount(Address address) =>
        _innerReader.GetAccount(address);

    public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
    {
        AssociativeCache<StorageCell, CachedSlot>? cache = _storageCache;
        if (cache is null)
        {
            return _innerReader.TryGetSlot(address, in slot, ref outValue);
        }

        StorageCell cell = new(address, in slot);
        // AssociativeCache is thread-safe by design (lock-free seqlock reads, set-local write
        // gates — see its class docs) and is shared by every reader of this persistence.
        // Set() only ever stores non-null values, so the null check is purely defensive.
        if (cache.TryGet(in cell, out CachedSlot? cached) && cached is not null)
        {
            Metrics.FlatStorageReadCacheHits++;
            if (!cached.Found) return false;
            outValue = cached.Value;
            return true;
        }

        Metrics.FlatStorageReadCacheMisses++;
        bool found = _innerReader.TryGetSlot(address, in slot, ref outValue);
        cache.Set(in cell, new CachedSlot(found ? outValue : default, found));
        return found;
    }

    public StateId CurrentState => _innerReader.CurrentState;

    public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) =>
        _innerReader.TryLoadStateRlp(in path, flags);

    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) =>
        _innerReader.TryLoadStorageRlp(address, in path, flags);

    public byte[]? GetAccountRaw(in ValueHash256 addrHash) =>
        _innerReader.GetAccountRaw(addrHash);

    public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) =>
        _innerReader.TryGetStorageRaw(addrHash, slotHash, ref value);

    public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) =>
        _innerReader.CreateAccountIterator(startKey, endKey);

    public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
        _innerReader.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);

    public bool IsPreimageMode => _innerReader.IsPreimageMode;

    protected override void CleanUp()
    {
        CancellationTokenExtensions.CancelDisposeAndClear(ref _cts);
        _innerReader.Dispose();
    }

    public bool TryAcquire() => TryAcquireLease();
}

/// <summary>
/// A cached storage-slot read result: the slot value and whether the slot existed.
/// Caching the "not found" case too avoids repeated backend lookups for absent slots.
/// </summary>
public sealed class CachedSlot(SlotValue value, bool found)
{
    public SlotValue Value { get; } = value;
    public bool Found { get; } = found;
}
