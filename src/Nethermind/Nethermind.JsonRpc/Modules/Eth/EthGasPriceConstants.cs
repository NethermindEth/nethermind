using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public static class EthGasPriceConstants
    {
        public const int PercentileOfSortedTxs = 60; //Percentile of sortedTxList indexes to choose as gas price
        public const int DefaultBlocksLimit = 20; //Limit for how many blocks we check txs in to add to sortedTxList
        public const int DefaultGasPrice = 1; //Tx price added to sortedTxList for a block that has 0 txs (now adds 1 price)
        public const int TxLimitFromABlock = 3; //Maximum number of tx we can add to sortedTxList from one block
        public static readonly UInt256 _maxGasPrice = 500; //Maximum gas price we can return
    }
}
