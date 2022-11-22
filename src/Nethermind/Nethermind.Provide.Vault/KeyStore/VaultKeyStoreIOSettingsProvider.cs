// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.KeyStore;
using Nethermind.Vault.Config;

namespace Nethermind.Vault.KeyStore
{
    public class VaultKeyStoreIOSettingsProvider : BaseKeyStoreIOSettingsProvider, IKeyStoreIOSettingsProvider
    {
        // move that consts to config if we will use KeyStore for Vault
        private const string VaultKeyStoreDirectory = "vaultkeystore";
        private readonly IVaultConfig _config;

        public VaultKeyStoreIOSettingsProvider(
            IVaultConfig vaultConfig)
        {
            _config = vaultConfig ?? throw new ArgumentNullException(nameof(vaultConfig));
        }

        public string StoreDirectory => GetStoreDirectory(VaultKeyStoreDirectory);

        public string KeyName => "vault key";

        public string GetFileName(Address address)
        {
            // $"Vault_UTC--{utcNow:yyyy-MM-dd}T{utcNow:HH-mm-ss.ffffff}000Z--{address.ToString(false, false)}";
            var utcNow = DateTime.UtcNow;
            return $"Vault_UTC--{utcNow:yyyy-MM-dd}T{utcNow:HH-mm-ss.ffffff}000Z--{address}";
        }
    }
}
