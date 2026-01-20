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

    public Account? GetAccount(Address address)
    {
        return null;
    }

    public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
    {
        return false;
    }

    public StateId CurrentState => new StateId(0, Keccak.EmptyTreeHash);

    public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags)
    {
        return null;
    }

    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags)
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

    public IPersistence.IFlatIterator CreateAccountIterator()
    {
        return new EmptyIterator();
    }

    public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey)
    {
        return new EmptyIterator();
    }

    public bool IsPreimageMode => false;

    private struct EmptyIterator : IPersistence.IFlatIterator
    {
        public bool MoveNext() => false;
        public ValueHash256 CurrentKey => default;
        public ReadOnlySpan<byte> CurrentValue => default;
        public void Dispose() { }
    }
}
