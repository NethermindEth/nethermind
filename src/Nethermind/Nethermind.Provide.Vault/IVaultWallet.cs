// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Vault
{
    public interface IVaultWallet
    {
        Task<Address[]> GetAccounts();

        Task<Address> CreateAccount();

        Task DeleteAccount(Address address);

        Task<Signature> Sign(Address address, Keccak message);

        Task<bool> Verify(Address address, Keccak message, Signature signature);
    }
}
