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
using System.Threading.Tasks;
using Nethermind.Vault.Styles;

namespace Nethermind.Vault
{
    public interface IVaultManager
    {
        Task<string> NewVault(Dictionary<string, object> parameters);
        Task<string[]> GetVaults();
        Task DeleteVault(string vaultId);
        public Task<string> NewVault(VaultArgs args)
        {
            return NewVault(new Dictionary<string, object> 
            {
                {
                    "vaultArgs", args
                }
            });
        }
    }
}