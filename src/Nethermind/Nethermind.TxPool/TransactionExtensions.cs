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

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Int256;

[assembly:InternalsVisibleTo("Nethermind.TxPool.Test")]

namespace Nethermind.TxPool
{
    internal static class TransactionExtensions
    {
        public static UInt256 CalculateAffordableGasPrice(this Transaction tx, bool eip1559Enabled, UInt256 baseFee, UInt256 balance)
        {
            if (eip1559Enabled && tx.IsEip1559)
            {
                if (balance > tx.Value && tx.GasLimit > 0)
                {
                    UInt256 effectiveGasPrice = tx.CalculateEffectiveGasPrice(eip1559Enabled, baseFee);
                    effectiveGasPrice.Multiply((UInt256)tx.GasLimit, out UInt256 gasCost);
                            
                    if (balance >= tx.Value + gasCost)
                    {
                        return effectiveGasPrice;
                    }
            
                    UInt256 balanceAvailableForFeePayment = balance - tx.Value;
                    balanceAvailableForFeePayment.Divide((UInt256)tx.GasLimit, out UInt256 payablePricePerGasUnit);
                    return payablePricePerGasUnit;
                }
                        
                return 0;
            }
            
            return tx.GasPrice;
        }
    }
}
