using System;
using System.IO;
using Nethermind.Core;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;

namespace Nethermind.KeyStore
{
    public class PrivateKeyStoreIOSettingsProvider : IKeyStoreIOSettingsProvider
    {
        private readonly IKeyStoreConfig _config;
        public PrivateKeyStoreIOSettingsProvider(
            IKeyStoreConfig keyStoreConfig)
        {
            _config = keyStoreConfig ?? throw new ArgumentNullException(nameof(keyStoreConfig));
        }
        public string StoreDirectory
        {
            get
            {
                var directory = _config.KeyStoreDirectory.GetApplicationResourcePath();
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                return directory;
            }
        }

        public string GetFileName(Address address)
        {
            //var keyDestinyStr = string.Empty;
            //if (keyDestiny != KeyDestiny.PrivateKey)
            //{
            //    keyDestinyStr = "--" + keyDestiny.ToString();
            //}
            //// "UTC--2018-12-30T14-04-11.699600594Z--1a959a04db22b9f4360db07125f690449fa97a83"
            //// "UTC--2018-12-30T14-04-11.699600594Z--1a959a04db22b9f4360db07125f690449fa97a83--Vault"
            //DateTime utcNow = DateTime.UtcNow;
            //string keyFileName = $"UTC--{utcNow:yyyy-MM-dd}T{utcNow:HH-mm-ss.ffffff}000Z--{address.ToString(false, false)}{keyDestinyStr}";

            // "UTC--2018-12-30T14-04-11.699600594Z--1a959a04db22b9f4360db07125f690449fa97a83"
            DateTime utcNow = DateTime.UtcNow;
            return $"UTC--{utcNow:yyyy-MM-dd}T{utcNow:HH-mm-ss.ffffff}000Z--{address.ToString(false, false)}";
        }
    }
}
