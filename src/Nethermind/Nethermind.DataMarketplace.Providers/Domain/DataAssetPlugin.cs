using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Providers.Domain
{
    public class DataAssetPlugin
    {
        public Keccak DataAssetId { get; private set; }
        public string Name { get; private set; }

        public DataAssetPlugin(Keccak dataAssetId, string name)
        {
            DataAssetId = dataAssetId;
            Name = name;
        }
    }
}