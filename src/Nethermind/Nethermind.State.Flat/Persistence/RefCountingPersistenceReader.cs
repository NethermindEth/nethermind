// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

public class RefCountingPersistenceReader : RefCountingDisposable, IPersistence.IPersistenceReader
{
    private const int AccountCacheCapacity = 1 << 18;
    private const int StorageCacheCapacity = 1 << 20;
    private const int NoAccessors = 0; // Same as parent's constant
    private const int Disposing = -1; // Same as parent's constant
    private readonly IPersistence.IPersistenceReader _innerReader;
    private readonly AssociativeCache<AddressAsKey, CachedAccount> _accountCache = new(AccountCacheCapacity);
    private readonly AssociativeCache<StorageCell, CachedSlot> _slotCache = new(StorageCacheCapacity);
    private CancellationTokenSource? _cts = new();
    public RefCountingPersistenceReader(IPersistence.IPersistenceReader innerReader, ILogger logger)
    {
        _innerReader = innerReader;

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

    public Account? GetAccount(Address address)
    {
        AddressAsKey key = address;
        if (_accountCache.TryGet(in key, out CachedAccount? cachedAccount) && cachedAccount is not null)
        {
            return cachedAccount.Value;
        }

        Account? account = _innerReader.GetAccount(address);
        _accountCache.Set(in key, new CachedAccount(account));
        return account;
    }

    public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
    {
        StorageCell key = new(address, in slot);
        if (_slotCache.TryGet(in key, out CachedSlot? cachedSlot) && cachedSlot is not null)
        {
            outValue = cachedSlot.Value;
            return cachedSlot.Found;
        }

        bool found = _innerReader.TryGetSlot(address, in slot, ref outValue);
        _slotCache.Set(in key, new CachedSlot(found, outValue));
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

    private sealed class CachedAccount(Account? value)
    {
        public Account? Value { get; } = value;
    }

    private sealed class CachedSlot(bool found, SlotValue value)
    {
        public bool Found { get; } = found;
        public SlotValue Value { get; } = value;
    }
}
