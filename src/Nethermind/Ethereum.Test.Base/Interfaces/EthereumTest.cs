using Nethermind.Core.Specs;
using Nethermind.Specs;

namespace Ethereum.Test.Base.Interfaces
{
    public abstract class EthereumTest
    {
        public string? Category { get; set; }
        public string? Name { get; set; }
        public string? LoadFailure { get; set; }
        public ulong ChainId { get; set; } = MainnetSpecProvider.Instance.ChainId;
        public IReleaseSpec GenesisSpec => ChainId == MainnetSpecProvider.Instance.ChainId
            ? MainnetSpecProvider.Instance.GenesisSpec
            : GnosisSpecProvider.Instance.GenesisSpec;
    }
}
