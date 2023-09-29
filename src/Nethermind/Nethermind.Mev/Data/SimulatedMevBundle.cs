// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

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
