using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class FeeHistoryResult
    {
        private long _oldestBlock;
        private UInt256[][] _reward;
        private UInt256[] _baseFee;
        private UInt256[] _gasUsedRatio;

        public FeeHistoryResult(long oldestBlock, UInt256[][] reward, UInt256[] baseFee, UInt256[] gasUsedRatio)
        {
            _oldestBlock = oldestBlock;
            _reward = reward;
            _baseFee = baseFee;
            _gasUsedRatio = gasUsedRatio;
        }
    }
}
