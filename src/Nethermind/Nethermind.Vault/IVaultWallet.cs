//  Copyright (c) 2020 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Security;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Vault.Styles;

namespace Nethermind.Vault
{
    public interface IVaultWallet
    {
        Task<Address[]> GetAccounts();
        Task<Address> NewAccount(Dictionary<string, object> parameters);
        Task DeleteAccount(Address address);
        Task<Signature> Sign(Address address, Keccak message);

        Task<string> SetWalletVault();

        public Task<Address> NewAccount(KeyArgs args) 
        {
            return NewAccount(new Dictionary<string, object> 
            {
                {
                    "keyArgs", args
                }
            });
        }
    }
}