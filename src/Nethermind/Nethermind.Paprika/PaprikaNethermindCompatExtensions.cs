// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State;
using Keccak = Paprika.Crypto.Keccak;

namespace Nethermind.Paprika;

public static class PaprikaNethermindCompatExtensions
{
    public static Keccak ToPaprikaKeccak(this Hash256 hash)
    {
        return new Keccak(hash.Bytes);
    }

    public static Keccak ToPaprikaKeccak(this ValueHash256 hash)
    {
        return new Keccak(hash.Bytes);
    }

    public static Keccak ToPaprikaKeccak(this Address address)
    {
        return address.ToAccountPath.ToPaprikaKeccak();
    }

    public static Keccak SlotToPaprikaKeccak(this UInt256 slot)
    {
        var key = new Keccak();
        StorageTree.ComputeKeyWithLookup(slot, key.BytesAsSpan);
        return key;
    }

    public static Hash256 ToNethHash(this Keccak hash)
    {
        return new Hash256(hash.BytesAsSpan);
    }

    public static Account ToNethAccount(this global::Paprika.Account account)
    {
        return new Account(
            account.Nonce,
            account.Balance,
            account.StorageRootHash.ToNethHash(),
            account.CodeHash.ToNethHash()
        );
    }

    public static AccountStruct ToNethAccountStruct(this global::Paprika.Account account)
    {
        return new AccountStruct(
            account.Nonce,
            account.Balance,
            account.StorageRootHash.ToNethHash(),
            account.CodeHash.ToNethHash()
        );
    }

    public static global::Paprika.Account ToPaprikaAccount(this Account account)
    {
        return new global::Paprika.Account(
            account.Balance,
            account.Nonce,
            account.CodeHash.ToPaprikaKeccak(),
            account.StorageRoot.ToPaprikaKeccak()
        );
    }
}
