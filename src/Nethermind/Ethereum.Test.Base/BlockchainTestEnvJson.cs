using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Ethereum.Test.Base
{
    public class BlockchainTestEnvJson
    {
        public Address CurrentCoinbase { get; set; }
        public UInt256 CurrentDifficulty { get; set; }
        public long CurrentGasLimit { get; set; }
        public long CurrentNumber { get; set; }
        public UInt256 CurrentTimestamp { get; set; }
        public Keccak PreviousHash { get; set; }
    }
}