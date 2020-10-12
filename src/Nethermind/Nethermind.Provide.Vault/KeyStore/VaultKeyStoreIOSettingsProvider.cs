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
