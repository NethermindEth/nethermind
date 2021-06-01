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

using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Comparers
{
    public static class GasPriceTxComparerHelper
    {
        public static int Compare(WrappedTransaction? x, WrappedTransaction? y, UInt256 baseFee, bool isEip1559Enabled)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;
            
            // if gas bottleneck was calculated, it's highest priority for sorting
            // if not, different method of sorting by gas price is needed
            if (x.GasBottleneck != 0 || y.GasBottleneck != 0)
            {
                return y.GasBottleneck.CompareTo(x.GasBottleneck);
            }

            // EIP1559 changed the way we're sorting transactions. The transaction with a higher miner tip should go first
            if (isEip1559Enabled)
            {
                UInt256 xGasPrice = UInt256.Min(x.Tx.MaxFeePerGas, x.Tx.MaxPriorityFeePerGas + baseFee);
                UInt256 yGasPrice = UInt256.Min(y.Tx.MaxFeePerGas, y.Tx.MaxPriorityFeePerGas + baseFee);
                if (xGasPrice < yGasPrice) return 1;
                if (xGasPrice > yGasPrice) return -1;

                return y.Tx.MaxFeePerGas.CompareTo(x.Tx.MaxFeePerGas);
            }
            
            // the old way of sorting transactions
            return y.Tx.GasPrice.CompareTo(x.Tx.GasPrice);
        }

    }
}
