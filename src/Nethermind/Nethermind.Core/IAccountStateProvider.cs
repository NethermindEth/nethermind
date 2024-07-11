// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public interface IAccountStateProvider
    {
        bool TryGetAccount(Address address, out AccountStruct account);

        [SkipLocalsInit]
        UInt256 GetNonce(Address address)
        {
            TryGetAccount(address, out AccountStruct account);
            return account.Nonce;
        }

        [SkipLocalsInit]
        UInt256 GetBalance(Address address)
        {
            TryGetAccount(address, out AccountStruct account);
            return account.Balance;
        }

        [SkipLocalsInit]
        ValueHash256 GetStorageRoot(Address address)
        {
            TryGetAccount(address, out AccountStruct account);
            return account.StorageRoot;
        }

        [SkipLocalsInit]
        ValueHash256 GetCodeHash(Address address)
        {
            TryGetAccount(address, out AccountStruct account);
            return account.CodeHash;
        }
    }
}
