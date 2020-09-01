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

using System.Threading.Tasks;
using provide.Model.Vault;

namespace Nethermind.Vault
{
    public interface IVaultService
    {
        Task<string[]> ListVaultIds();
        
        Task<string> CreateVault(provide.Model.Vault.Vault vault);

        Task DeleteVault(string vaultId);
        
        Task ResetToken(string token);

        Task<Key> CreateKey(string vaultId, Key key);

        Task<Key> DeleteKey(string vaultId, string keyId);

        Task<string> Sign(string vaultId, string keyId, string message);

        Task<bool> Verify(string vaultId, string keyId, string message, string signature);
    }
}