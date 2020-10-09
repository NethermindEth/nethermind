using Nethermind.Core;

namespace Nethermind.KeyStore
{
    public interface IKeyStoreIOSettingsProvider
    {
        public string StoreDirectory { get; }

        public string GetFileName(Address address);
    }
}
