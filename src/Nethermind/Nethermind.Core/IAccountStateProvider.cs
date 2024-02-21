// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public interface IAccountStateProvider
    {
        bool TryGetAccount(Address address, out AccountStruct account);

        bool IsContract(Address address) => TryGetAccount(address, out AccountStruct account) && account.IsContract;
        UInt256 GetNonce(Address address) => TryGetAccount(address, out AccountStruct account) ? account.Nonce : UInt256.Zero;
        UInt256 GetBalance(Address address) => TryGetAccount(address, out AccountStruct account) ? account.Balance : UInt256.Zero;
        ValueHash256 GetStorageRoot(Address address) => TryGetAccount(address, out AccountStruct account) ? account.StorageRoot : Keccak.EmptyTreeHash.ValueHash256;
        ValueHash256 GetCodeHash(Address address) => TryGetAccount(address, out AccountStruct account) ? account.CodeHash : Keccak.OfAnEmptyString.ValueHash256;
        bool AccountExists(Address address) => TryGetAccount(address, out _);
        bool IsEmptyAccount(Address address) => !TryGetAccount(address, out AccountStruct account) || account.IsEmpty;
        bool IsDeadAccount(Address address) => !TryGetAccount(address, out AccountStruct account) || account.IsEmpty;
    }
}
