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
using System.Collections;
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
        public SimulatedMevBundle(
            MevBundle bundle,
            long gasUsed,
            bool success,
            UInt256 bundleFee,
            UInt256 coinbasePayments, 
            UInt256 eligibleGasFeePayment)
        {
            Bundle = bundle;
            GasUsed = gasUsed;
            Success = success;
            BundleFee = bundleFee;
            CoinbasePayments = coinbasePayments;
            EligibleGasFeePayment = eligibleGasFeePayment;
        }

        public UInt256 CoinbasePayments { get; }
        
        public UInt256 BundleFee { get; }
        
        public UInt256 EligibleGasFeePayment { get; }
        
        public UInt256 Profit => BundleFee + CoinbasePayments;

        public MevBundle Bundle { get; }

        public bool Success { get; }

        public long GasUsed { get; }
        
        public UInt256 BundleScoringProfit => EligibleGasFeePayment + CoinbasePayments;

        public UInt256 BundleAdjustedGasPrice => BundleScoringProfit / (UInt256)GasUsed;

        public static SimulatedMevBundle Cancelled(MevBundle bundle) =>
            new SimulatedMevBundle(bundle,
                0,
                false,
                UInt256.Zero,
                UInt256.Zero,
                UInt256.Zero);
    }
}
