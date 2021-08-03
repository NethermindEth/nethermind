using System;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class FeeHistoryResults
    {
        public long OldestBlock { get; }
        public UInt256[][]? Reward { get; }
        public UInt256[]? BaseFeePerGas { get; }
        public double[]? GasUsedRatio { get; }
        
        public FeeHistoryResults(long oldestBlock, UInt256[] baseFeePerGas, double[] gasUsedRatio, UInt256[][]? reward = null)
        {
            OldestBlock = oldestBlock;
            Reward = reward;
            BaseFeePerGas = baseFeePerGas;
            GasUsedRatio = gasUsedRatio;
        }
    }
}
