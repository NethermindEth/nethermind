// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

public class NoopPersistenceReader: IPersistence.IPersistenceReader
{
    public void Dispose()
    {
    }

    public bool TryGetAccount(Address address, out Account? acc)
    {
        acc = null;
        return false;
    }

    public bool TryGetSlot(Address address, in UInt256 index, out byte[] value)
    {
        value = null;
        return false;
    }

    public StateId CurrentState => new StateId(0, Keccak.EmptyTreeHash);
    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        return null;
    }

    public byte[]? GetAccountRaw(Hash256? addrHash)
    {
        return null;
    }

    public byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash)
    {
        return null;
    }
}
