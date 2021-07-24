using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class FeeHistoryResult
    {
        private long? _oldestBlock;
        public UInt256[]?[]? _reward;
        public UInt256[]? _baseFee;
        public float[]? _gasUsedRatio;
        public const int MaxHistory = 1;

        public FeeHistoryResult(long? oldestBlock = null, UInt256[][]? reward = null, UInt256[]? baseFee = null, float[]? gasUsedRatio = null)
        {
            _oldestBlock = oldestBlock;
            _reward = reward;
            _baseFee = baseFee;
            _gasUsedRatio = gasUsedRatio;
        }
    }
}
