
//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth.GasPrice
{
    public static class EthGasPriceConstants
    {
        public const int PercentileOfSortedTxs = 60; //Percentile of sortedTxList indexes to choose as gas price
        public const int DefaultBlocksLimit = 20; //Limit for how many blocks we check txs in to add to sortedTxList
        public static readonly UInt256 DefaultGasPrice = 20.GWei(); //Tx price added to sortedTxList for a block that has 0 txs (now adds 1 price)
        public const int TxLimitFromABlock = 3; //Maximum number of tx we can add to sortedTxList from one block
        public const int DefaultIgnoreUnder = 2; //Effective Gas Prices under this are ignored
        public static readonly UInt256 MaxGasPrice = 500.GWei(); //Maximum gas price we can return
    }
}
