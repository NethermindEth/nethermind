// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

internal static class PbtStateKey
{
    public static PbtFullKey Account(Address address, byte subIndex) =>
        Eip8297KeyDerivation.AccountKey(Address32(address), subIndex);

    public static PbtFullKey Storage(Address address, in UInt256 slot) =>
        Eip8297KeyDerivation.StorageKey(Address32(address), slot);

    public static PbtFullKey Code(Address address, in ValueHash256 codeHash, int chunkId) =>
        Eip8297KeyDerivation.CodeKey(Address32(address), codeHash.Bytes, chunkId);

    public static PbtFullKey AccountPrefix(Address address)
    {
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(address);
        byte[] prefix = new byte[33];
        prefix[0] = Eip8297KeyDerivation.AccountZone;
        addressHash.Bytes.CopyTo(prefix.AsSpan(1));
        return new PbtFullKey(prefix);
    }

    public static PbtFullKey StoragePrefix(Address address)
    {
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(address);
        byte[] prefix = new byte[33];
        prefix[0] = Eip8297KeyDerivation.StorageZone;
        addressHash.Bytes.CopyTo(prefix.AsSpan(1));
        return new PbtFullKey(prefix);
    }

    private static byte[] Address32(Address address)
    {
        byte[] address32 = new byte[32];
        address.Bytes.CopyTo(address32.AsSpan(12));
        return address32;
    }
}
