using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public static class GasPriceConfig
    {
        public const int NoHeadBlockChangeErrorCode = 7; //Error code used in debug mode when head block not changed
        public const int PercentileOfSortedTxs = 60; //Percentile of sorted tx list to choose as gas price
        public const int DefaultBlocksLimit = 20; //Limit for how many blocks we check txs in to add to sortedTxList
        public const int DefaultGasPrice = 1; //What we add to sortedTxList when a block has 0 txs
        public const int SoftTxLimit = 40; //If block has 0/1 tx, the block is not part of the blocklimit if
        //tsortedTxList.Count() + 1 + blocksLeftToCheck <= SoftTxLimit
        public const int DefaultBaseFee = 200;
        public const int TxLimitFromABlock = 3; //maximum number of tx we can get from a block
        public static readonly UInt256 _maxGasPrice = 500; //maximum gas price we can return
    }
}
