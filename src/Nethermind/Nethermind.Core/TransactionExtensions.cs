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

namespace Nethermind.Core
{
    public static class TransactionExtensions
    {
        public static bool IsSystem(this Transaction tx) => 
            tx is SystemTransaction || tx.SenderAddress == Address.SystemUser;

        private static bool IsFree(this Transaction tx) => tx.IsSystem() || tx.IsServiceTransaction;

        public static bool TryCalculatePremiumPerGas(this Transaction tx, UInt256 baseFeePerGas, out UInt256 premiumPerGas)
        {
            bool freeTransaction = tx.IsFree();
            UInt256 feeCap = tx.IsEip1559 ? tx.MaxFeePerGas : tx.GasPrice;
            if (baseFeePerGas > feeCap)
            {
                premiumPerGas = UInt256.Zero; 
                return freeTransaction;
            }
            
            premiumPerGas = UInt256.Min(tx.MaxPriorityFeePerGas, feeCap - baseFeePerGas);
            return true;
        }
        
        public static UInt256 CalculateTransactionPotentialCost(this Transaction tx, bool eip1559Enabled, UInt256 baseFee)
        {
            if (eip1559Enabled)
            {
                UInt256 gasPrice = baseFee + tx.MaxPriorityFeePerGas;
                gasPrice = UInt256.Min(gasPrice, tx.MaxFeePerGas);
                if (tx.IsServiceTransaction)
                    gasPrice = UInt256.Zero;
                
                return gasPrice * (ulong)tx.GasLimit + tx.Value;
            }

            return tx.GasPrice * (ulong)tx.GasLimit + tx.Value;
        }
        
        public static UInt256 CalculateEffectiveGasPrice(this Transaction tx, bool eip1559Enabled, UInt256 baseFee)
        {
            return eip1559Enabled ? UInt256.Min(tx.IsEip1559 ? tx.MaxFeePerGas : tx.GasPrice, tx.MaxPriorityFeePerGas + baseFee) : tx.GasPrice;
        }
    }
}
