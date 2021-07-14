using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Ethereum.Test.Base
{
    public class GeneralStateTestEnvJson
    {
        public Address CurrentCoinbase { get; set; }
        public UInt256 CurrentDifficulty { get; set; }
        public long CurrentGasLimit { get; set; }
        public long CurrentNumber { get; set; }
        public UInt256 CurrentTimestamp { get; set; }
        public UInt256? CurrentBaseFee { get; set; }
        public Keccak PreviousHash { get; set; }
    }
}
