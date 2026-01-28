// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public static class TransactionExtensions
    {
        extension(Transaction tx)
        {
            public bool IsSystem() => tx is SystemTransaction || tx.SenderAddress == Address.SystemUser || tx.IsOPSystemTransaction;

            public bool IsFree() => tx.IsSystem() || tx.IsServiceTransaction;

            public bool TryCalculatePremiumPerGas(in UInt256 baseFeePerGas, out UInt256 premiumPerGas)
            {
                bool freeTransaction = tx.IsFree();
                UInt256 feeCap = tx.Supports1559 ? tx.MaxFeePerGas : tx.MaxPriorityFeePerGas;
                if (baseFeePerGas > feeCap)
                {
                    premiumPerGas = default;
                    return freeTransaction;
                }

                UInt256.Subtract(feeCap, baseFeePerGas, out feeCap);
                premiumPerGas = UInt256.Min(tx.MaxPriorityFeePerGas, feeCap);
                return true;
            }

            public UInt256 CalculateTransactionPotentialCost(bool eip1559Enabled, in UInt256 baseFee)
            {
                if (eip1559Enabled)
                {
                    UInt256 effectiveGasPrice = tx.IsServiceTransaction switch
                    {
                        true => UInt256.Zero,
                        _ => tx.CalculateEffectiveGasPrice(eip1559Enabled, baseFee)
                    };

                    return effectiveGasPrice * (ulong)tx.GasLimit + tx.ValueRef;
                }

                return tx.MaxPriorityFeePerGas * (ulong)tx.GasLimit + tx.ValueRef;
            }

            public UInt256 CalculateEffectiveGasPrice(bool eip1559Enabled, in UInt256 baseFee) =>
                !eip1559Enabled ? tx.MaxPriorityFeePerGas
                : UInt256.AddOverflow(tx.MaxPriorityFeePerGas, baseFee, out UInt256 effectiveFee) ? tx.MaxFeePerGas
                : UInt256.Min(tx.MaxFeePerGas, effectiveFee);

            public UInt256 CalculateMaxPriorityFeePerGas(bool eip1559Enabled, in UInt256 baseFee) =>
                eip1559Enabled ? UInt256.Min(tx.MaxPriorityFeePerGas, tx.MaxFeePerGas > baseFee ? tx.MaxFeePerGas - baseFee : 0) : tx.MaxPriorityFeePerGas;

            public bool IsAboveInitCode(IReleaseSpec spec) =>
                tx.IsContractCreation && spec.IsEip3860Enabled && tx.DataLength > spec.MaxInitCodeSize;

            public ulong GetBlobGas() => (uint)tx.GetBlobCount() * Eip4844Constants.GasPerBlob;
            public int GetBlobCount() => tx.BlobVersionedHashes?.Length ?? 0;
        }

        public static bool TryGetByTxType<T>(this T?[] array, TxType txType, [NotNullWhen(true)] out T? item)
        {
            var type = (byte)txType;
            if (type > Transaction.MaxTxType)
            {
                item = default;
                return false;
            }

            item = array[type];
            return item != null;
        }
    }
}
