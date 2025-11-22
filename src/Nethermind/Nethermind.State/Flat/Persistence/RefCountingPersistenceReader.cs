// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Prometheus;

namespace Nethermind.State.Flat.Persistence;

public class RefCountingPersistenceReader : RefCountingDisposable, IPersistence.IPersistenceReader
{
    private readonly IPersistence.IPersistenceReader _innerReader;
    private bool _isDisposed = false;

    public RefCountingPersistenceReader(IPersistence.IPersistenceReader innerReader, ILogger logger)
    {
        _innerReader = innerReader;

        string sTrace = Environment.StackTrace;
        _ = Task.Run(async () =>
        {
            // Reader should be re-created every block unless something holds it for very long.
            // It prevent database compaction, so this need to be closed eventually.
            await Task.Delay(60_000);
            if (!_isDisposed)
            {
                if (logger.IsWarn) logger.Warn($"Unexpected old snapshot created. Lease count {_leases.Value} at {sTrace}");
            }
        });
    }

    public bool TryGetAccount(Address address, out Account? acc)
    {
        return _innerReader.TryGetAccount(address, out acc);
    }

    public bool TryGetSlot(Address address, in UInt256 index, out byte[] value)
    {
        return _innerReader.TryGetSlot(address, in index, out value);
    }

    public StateId CurrentState => _innerReader.CurrentState;

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        return _innerReader.TryLoadRlp(address, in path, hash, flags);
    }

    public byte[]? GetAccountRaw(Hash256? addrHash)
    {
        return _innerReader.GetAccountRaw(addrHash);
    }

    public byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash)
    {
        return _innerReader.GetStorageRaw(addrHash, slotHash);
    }

    protected override void CleanUp()
    {
        _isDisposed = true;
        _innerReader.Dispose();
    }
}
