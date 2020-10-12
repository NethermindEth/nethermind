using System;
using Nethermind.Core;
using Nethermind.KeyStore;

namespace Nethermind.Vault.KeyStore
{
    class VaultKeyStoreIOSettingsProvider : IKeyStoreIOSettingsProvider
    {
        public string StoreDirectory => throw new NotImplementedException();

        public string GetFileName(Address address)
        {
            throw new NotImplementedException();
        }
    }
}
