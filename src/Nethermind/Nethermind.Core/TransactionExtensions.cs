// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public static class TransactionExtensions
    {
        public static bool IsSystem(this Transaction tx) =>
            tx is SystemTransaction || tx.SenderAddress == Address.SystemUser || tx.IsOPSystemTransaction;

        public static bool IsFree(this Transaction tx) => tx.IsSystem() || tx.IsServiceTransaction;

        public static bool TryCalculatePremiumPerGas(this Transaction tx, in UInt256 baseFeePerGas, out UInt256 premiumPerGas)
        {
            bool freeTransaction = tx.IsFree();
            UInt256 feeCap = tx.Supports1559 ? tx.MaxFeePerGas : tx.GasPrice;
            if (baseFeePerGas > feeCap)
            {
                premiumPerGas = UInt256.Zero;
                return freeTransaction;
            }

            premiumPerGas = UInt256.Min(tx.MaxPriorityFeePerGas, feeCap - baseFeePerGas);
            return true;
        }

        public static UInt256 CalculateTransactionPotentialCost(this Transaction tx, bool eip1559Enabled, in UInt256 baseFee)
        {
            if (eip1559Enabled)
            {
                UInt256 effectiveGasPrice = tx.CalculateEffectiveGasPrice(eip1559Enabled, baseFee);
                if (tx.IsServiceTransaction)
                    effectiveGasPrice = UInt256.Zero;

                return effectiveGasPrice * (ulong)tx.GasLimit + tx.Value;
            }

            return tx.GasPrice * (ulong)tx.GasLimit + tx.Value;
        }

        public static UInt256 CalculateEffectiveGasPrice(this Transaction tx, bool eip1559Enabled, in UInt256 baseFee)
        {
            UInt256 effectiveGasPrice = tx.GasPrice;
            if (eip1559Enabled)
            {
                if (UInt256.AddOverflow(tx.MaxPriorityFeePerGas, baseFee, out UInt256 effectiveFee))
                {
                    return tx.MaxFeePerGas;
                }

                effectiveGasPrice = UInt256.Min(tx.MaxFeePerGas, effectiveFee);
            }

            return effectiveGasPrice;
        }

        public static UInt256 CalculateMaxPriorityFeePerGas(this Transaction tx, bool eip1559Enabled, in UInt256 baseFee)
        {
            return eip1559Enabled ? UInt256.Min(tx.MaxPriorityFeePerGas, tx.MaxFeePerGas > baseFee ? tx.MaxFeePerGas - baseFee : 0) : tx.MaxPriorityFeePerGas;
        }
        public static bool IsAboveInitCode(this Transaction tx, IReleaseSpec spec)
        {
            return tx.IsContractCreation && spec.IsEip3860Enabled && (tx.DataLength) > spec.MaxInitCodeSize;
        }


    }
}
