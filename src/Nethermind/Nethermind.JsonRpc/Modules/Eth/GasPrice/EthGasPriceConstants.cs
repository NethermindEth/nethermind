// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth.GasPrice
{
    public static class EthGasPriceConstants
    {
        public const int PercentileOfSortedTxs = 60; //Percentile of sortedTxList indexes to choose as gas price
        public const int DefaultBlocksLimit = 20; //Limit for how many blocks we check txs in to add to sortedTxList
        public const int DefaultBlocksLimitMaxPriorityFeePerGas = 100; //Limit for how many blocks we check txs in to add to sortedTxList in case of calculating max priority fee per gas
        public const int TxLimitFromABlock = 3; //Maximum number of tx we can add to sortedTxList from one block
        public const int DefaultIgnoreUnder = 2; //Effective Gas Prices under this are ignored
        public static readonly UInt256 MaxGasPrice = 500.GWei(); //Maximum gas price we can return
        public static readonly UInt256 FallbackMaxPriorityFeePerGas = 200.GWei();
    }
}
