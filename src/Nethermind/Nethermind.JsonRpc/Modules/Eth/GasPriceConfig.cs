namespace Nethermind.JsonRpc.Modules.Eth
{
    public static class GasPriceConfig
    {
        public const int NoHeadBlockChangeErrorCode = 7;
        public const int Percentile = 40;
        public const int DefaultBlocksToGoBack = 20;
        public const int DefaultBaseFee = 200;
        public const int BlockLimitForDefaultGasPrice = 8;
        public const int MaxTxsToGetInEachBlock = 3;
        public const int MaxGasPrice = 500;
    }
}
