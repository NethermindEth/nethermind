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

namespace Nethermind.Vault.Config
{
    public class VaultConfig : IVaultConfig
    {
        public bool Enabled { get; set;}
        public string Host { get; set; } = "vault.provide.services";
        public string Token { get; set;}
        public string Scheme { get; set; } = "https";
        public string Path { get; set; } = "api/v1";
        public string VaultId { get; set;}
        public string VaultKeyFile { get; set; }
        public string VaultSealUnsealKey { get; set; }
        public string NChainHost { get; set; }
        public string NChainToken { get; set; }
        public string NChainScheme { get; set; }
        public string NChainPath { get; set; }
        public string NChainNetworkId { get; set; }
        public string NChainAccountId { get; set; }
    }
}
