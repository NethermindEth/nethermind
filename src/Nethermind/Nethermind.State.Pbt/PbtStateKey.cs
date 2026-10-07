// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

internal static class PbtStateKey
{
    /// <summary>BLAKE3 of the 32-byte left-padded address; the flat account/storage column key.</summary>
    public static ValueHash256 AddressKeyHash(Address address)
    {
        Span<byte> address32 = stackalloc byte[32];
        Address32(address, address32);
        return Blake3Hash.Hash(address32);
    }

    public static PbtPath Account(in ValueHash256 addressHash, byte subIndex) =>
        Eip8297KeyDerivation.AccountKey(addressHash, subIndex);

    public static PbtPath Code(in ValueHash256 codeHash, int chunkId) =>
        Eip8297KeyDerivation.OverflowCodeKey(codeHash.Bytes, chunkId);

    public static PbtStorageTreeKey Storage(Address address, in UInt256 slot)
    {
        Span<byte> address32 = stackalloc byte[32];
        Address32(address, address32);
        return Eip8297KeyDerivation.StorageKey(address32, slot);
    }

    /// <summary><see cref="Storage(Address, in UInt256)"/> reusing a precomputed <see cref="AddressKeyHash"/>.</summary>
    public static PbtStorageTreeKey Storage(Address address, in ValueHash256 addressHash, in UInt256 slot)
    {
        Span<byte> address32 = stackalloc byte[32];
        Address32(address, address32);
        return Eip8297KeyDerivation.StorageKey(address32, addressHash, slot);
    }

    /// <summary>The <see cref="SlotRun.RunKey"/> of the slot's storage key, and the slot's index within the run.</summary>
    public static PbtStorageTreeKey StorageRun(Address address, in ValueHash256 addressHash, in UInt256 slot, out int index)
    {
        PbtStorageTreeKey slotKey = Storage(address, addressHash, slot);
        index = SlotRun.IndexOf(slotKey);
        return SlotRun.RunKey(slotKey);
    }

    internal static ValueHash256 StorageAddress(in PbtStorageTreeKey key) => new(key.Bytes.Slice(1, ValueHash256.MemorySize));

    private static void Address32(Address address, Span<byte> address32)
    {
        address32[..12].Clear();
        address.Bytes.CopyTo(address32[12..]);
    }
}
