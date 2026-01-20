// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

public class RefCountingPersistenceReader : RefCountingDisposable, IPersistence.IPersistenceReader
{
    private const int Disposing = -1; // Same as parent's constant
    private readonly IPersistence.IPersistenceReader _innerReader;

    public RefCountingPersistenceReader(IPersistence.IPersistenceReader innerReader, ILogger logger)
    {
        _innerReader = innerReader;

        _ = Task.Run(async () =>
        {
            // Reader should be re-created every block unless something holds it for very long.
            // It prevent database compaction, so this need to be closed eventually.
            await Task.Delay(60_000);
            if (Volatile.Read(ref _leases.Value) != Disposing)
            {
                if (logger.IsWarn) logger.Warn($"Unexpected old snapshot created. Lease count {_leases.Value}");
            }
        });
    }

    public Account? GetAccount(Address address)
    {
        return _innerReader.GetAccount(address);
    }

    public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
    {
        return _innerReader.TryGetSlot(address, in slot, ref outValue);
    }

    public StateId CurrentState => _innerReader.CurrentState;

    public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags)
    {
        return _innerReader.TryLoadStateRlp(in path, flags);
    }

    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags)
    {
        return _innerReader.TryLoadStorageRlp(address, in path, flags);
    }

    public byte[]? GetAccountRaw(Hash256 addrHash)
    {
        return _innerReader.GetAccountRaw(addrHash);
    }

    public byte[]? GetStorageRaw(Hash256 addrHash, Hash256 slotHash)
    {
        return _innerReader.GetStorageRaw(addrHash, slotHash);
    }

    public IPersistence.IFlatIterator CreateAccountIterator()
    {
        return _innerReader.CreateAccountIterator();
    }

    public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey)
    {
        return _innerReader.CreateStorageIterator(accountKey);
    }

    public bool IsPreimageMode => _innerReader.IsPreimageMode;

    protected override void CleanUp()
    {
        _innerReader.Dispose();
    }

    public bool TryAcquire()
    {
        return TryAcquireLease();
    }
}
