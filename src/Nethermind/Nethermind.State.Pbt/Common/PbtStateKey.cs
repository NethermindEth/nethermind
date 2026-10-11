// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Common;

public static class PbtStateKey
{
    /// <summary>The <see cref="Eip8297KeyDerivation.AddressHash"/> of <paramref name="address"/>; the flat account/storage column key.</summary>
    public static ValueHash256 AddressKeyHash(Address address)
    {
        ValueHash256 address32 = address.ToHash();
        return Eip8297KeyDerivation.AddressHash(address32.Bytes);
    }

    /// <summary>The non-header storage leaf key; takes the precomputed <see cref="AddressKeyHash"/>.</summary>
    public static PbtStoragePath Storage(Address address, in ValueHash256 addressHash, in UInt256 slot)
    {
        ValueHash256 address32 = address.ToHash();
        return Eip8297KeyDerivation.StorageKey(address32.Bytes, addressHash, slot);
    }

    /// <summary>The <see cref="Eip8297KeyDerivation.SlotKey"/> of <paramref name="address"/>; takes the precomputed <see cref="AddressKeyHash"/>.</summary>
    public static PbtVariableTreeKey Slot(Address address, in ValueHash256 addressHash, in UInt256 slot)
    {
        ValueHash256 address32 = address.ToHash();
        return Eip8297KeyDerivation.SlotKey(address32.Bytes, addressHash, slot);
    }

    /// <summary>The canonical tree leaves of an account, derived from its whole flat value.</summary>
    public static IEnumerable<KeyValuePair<PbtPath, ValueHash256>> AccountLeaves(ValueHash256 addressHash, Account account, CodeInfo? code)
    {
        PbtAccount stem = PbtAccount.From(account, code);
        if (stem.BasicData != default) yield return new(Eip8297KeyDerivation.AccountKey(addressHash, PbtKeyDerivation.BasicDataLeafKey), stem.BasicData);
        if (stem.IsDelegation)
        {
            yield return new(Eip8297KeyDerivation.AccountKey(addressHash, PbtKeyDerivation.DelegationLeafKey), stem.CodeLeaf);
            yield break;
        }
        yield return new(Eip8297KeyDerivation.AccountKey(addressHash, PbtKeyDerivation.CodeHashLeafKey), stem.CodeLeaf);
        if (code is null) yield break;
        foreach (KeyValuePair<PbtPath, ValueHash256> leaf in Eip8297KeyDerivation.CodeLeaves(stem.CodeLeaf, code.Code)) yield return leaf;
    }
}
