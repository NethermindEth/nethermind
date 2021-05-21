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
            UInt256 eligibleGasFeePayment)
        {
            Bundle = bundle;
            Success = success;
            FailedTransactions = failedTransactions;
            GasUsed = gasUsed;
            TxFees = txFees;
            CoinbasePayments = coinbasePayments;
            EligibleGasFeePayment = eligibleGasFeePayment;
            IEnumerable<Keccak?> unexpectedFailedTransactions = failedTransactions.Intersect(bundle.Transactions.Select(tx => tx.Hash));
            if (!unexpectedFailedTransactions.Any())
            {
                throw new Exception("Bundle includes failed transaction " + unexpectedFailedTransactions.ToString() + 
                                    " which is not in RevertingTxHashes");
            }
        }

        public UInt256 CoinbasePayments { get; set; }
        
        public UInt256 TxFees { get; set; }
        
        public UInt256 EligibleGasFeePayment { get; set; }

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
