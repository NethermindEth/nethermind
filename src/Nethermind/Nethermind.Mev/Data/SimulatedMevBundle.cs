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

using System;
using System.Linq;
using Nethermind.Int256;
using Nethermind.TxPool;
using Nethermind.Core;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Mev.Data
{
    public class SimulatedMevBundle
    {

        public SimulatedMevBundle(MevBundle bundle, 
            bool success,
            ICollection<Keccak> failedTransactions, 
            long gasUsed, 
            UInt256 txFees, 
            UInt256 coinbasePayments, 
            Dictionary<Keccak, UInt256> eligibleGasFeePaymentPerTransaction)
        {
            Bundle = bundle;
            Success = success;
            FailedTransactions = failedTransactions;
            GasUsed = gasUsed;
            TxFees = txFees;
            CoinbasePayments = coinbasePayments;
            EligibleGasFeePaymentPerTransaction = eligibleGasFeePaymentPerTransaction;
            IEnumerable<Keccak?> unexpectedFailedTransactions = failedTransactions.Where(tx => !bundle.RevertingTxHashes.Contains(tx));
            if (!unexpectedFailedTransactions.Any() && Success == false)
            {
                Success = true;
            }
        }

        public UInt256 CoinbasePayments { get; set; }
        
        public UInt256 TxFees { get; set; }
        
        public Dictionary<Keccak, UInt256> EligibleGasFeePaymentPerTransaction { get; set; }
        
        public UInt256 EligibleGasFeePayment
        {
            get
            {
                // UInt256 doesn't implement Sum for some reason? need to clean this up
                UInt256 sum = UInt256.Zero;
                foreach (UInt256 value in EligibleGasFeePaymentPerTransaction.Values)
                {
                    sum += value;
                }
                return sum;
            }
        }
        public UInt256 Profit => TxFees + CoinbasePayments;

        public MevBundle Bundle { get; }
        
        public bool Success { get; }

        public ICollection<Keccak> FailedTransactions { get; }

        public long GasUsed { get; set; }

        public UInt256 AdjustedGasPrice => Profit / (UInt256)GasUsed;
        
        public UInt256 BundleScoringProfit => EligibleGasFeePayment + CoinbasePayments;

        public UInt256 BundleAdjustedGasPrice => BundleScoringProfit / (UInt256)GasUsed;
    }
}
