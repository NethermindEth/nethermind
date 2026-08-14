// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
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

    public Account? GetAccount(Address address) =>
        _innerReader.GetAccount(address);

    public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue) =>
        _innerReader.TryGetSlot(address, in slot, ref outValue);

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
