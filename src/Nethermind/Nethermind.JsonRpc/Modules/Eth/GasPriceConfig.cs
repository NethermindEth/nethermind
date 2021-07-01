using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public static class GasPriceConfig
    {
        public const int NoHeadBlockChangeErrorCode = 7;
        public const int Percentile = 60;
        public const int DefaultBlocksToGoBack = 20;
        public const int DefaultBaseFee = 200;
        public const int BlockLimitForDefaultGasPrice = 8;
        public const int MaxTxsFromBlock = 3;
        public static readonly UInt256 _maxGasPrice = 500;
    }
}
