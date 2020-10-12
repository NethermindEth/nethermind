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

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Vault.Config;

namespace Nethermind.Vault.KeyStore
{
    public class VaultKeyStoreIOSettingsProvider : IKeyStoreIOSettingsProvider
    {
        private readonly IVaultConfig _config;

        public VaultKeyStoreIOSettingsProvider(
            IVaultConfig vaultConfig)
        {
            _config = vaultConfig ?? throw new ArgumentNullException(nameof(vaultConfig));
        }

        public string StoreDirectory
        {
            get
            {
                var directory = _config.VaultKeyStoreDirectory.GetApplicationResourcePath();
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                return directory;
            }
        }

        public string KeyName => "vault key";

        public string GetFileName(Address address)
        {
            // $"Vault_UTC--{utcNow:yyyy-MM-dd}T{utcNow:HH-mm-ss.ffffff}000Z--{address.ToString(false, false)}";
            var utcNow = DateTime.UtcNow;
            return $"Vault_UTC--{utcNow:yyyy-MM-dd}T{utcNow:HH-mm-ss.ffffff}000Z--{address}";
        }
    }
}
