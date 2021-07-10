using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public static class EthGasPriceConstants
    {
        public const int NoHeadBlockChangeErrorCode = 7; //Error code used in debug mode when the head block is not changed
        public const int PercentileOfSortedTxs = 60; //Percentile of sortedTxList indexes to choose as gas price
        public const int DefaultBlocksLimit = 20; //Limit for how many blocks we check txs in to add to sortedTxList
        public const int DefaultGasPrice = 1; //Tx price added to sortedTxList for a block that has 0 txs (now adds 1 tx)
        public const int SoftTxLimit = 40; //If a block adds only 1 tx price, the block is not counted towards BlocksLimit if sortedTxList.Count() + blocksLeftToCheck <= SoftTxLimit (after price is added to sortedTxList) 
        public const int DefaultBaseFee = 200; //Minimum price a transaction sender has to pay
        public const int TxLimitFromABlock = 3; //Maximum number of tx we can add to sortedTxList from one block
        public static readonly UInt256 _maxGasPrice = 500; //Maximum gas price we can return
    }
}
