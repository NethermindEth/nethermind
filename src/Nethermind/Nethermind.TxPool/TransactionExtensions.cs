// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

[assembly: InternalsVisibleTo("Nethermind.TxPool.Test")]

namespace Nethermind.TxPool
{
    public static class TransactionExtensions
    {
        private static readonly long MaxSizeOfTxForBroadcast = 4.KiB(); //4KB, as in Geth https://github.com/ethereum/go-ethereum/pull/27618
        private static readonly ITransactionSizeCalculator _transactionSizeCalculator = new NetworkTransactionSizeCalculator(TxDecoder.Instance);

        public static int GetLength(this Transaction tx, bool shouldCountBlobs = true)
        {
            return tx.GetLength(_transactionSizeCalculator, shouldCountBlobs);
        }

        public static bool CanPayBaseFee(this Transaction tx, UInt256 currentBaseFee) => tx.MaxFeePerGas >= currentBaseFee;

        public static bool CanPayForBlobGas(this Transaction tx, UInt256 currentPricePerBlobGas) => !tx.SupportsBlobs || tx.MaxFeePerBlobGas >= currentPricePerBlobGas;

        public static bool CanBeBroadcast(this Transaction tx) => !tx.SupportsBlobs && tx.GetLength() <= MaxSizeOfTxForBroadcast;

        internal static UInt256 CalculateGasPrice(this Transaction tx, bool eip1559Enabled, in UInt256 baseFee)
        {
            if (eip1559Enabled && tx.Supports1559)
            {
                if (tx.GasLimit > 0)
                {
                    return tx.CalculateEffectiveGasPrice(eip1559Enabled, baseFee);
                }

                return 0;
            }

            return tx.GasPrice;
        }

        internal static UInt256 CalculateAffordableGasPrice(this Transaction tx, bool eip1559Enabled, in UInt256 baseFee, in UInt256 balance)
        {
            if (eip1559Enabled && tx.Supports1559)
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

            return balance <= tx.Value ? default : tx.GasPrice;
        }

        internal static bool CheckForNotEnoughBalance(this Transaction tx, UInt256 currentCost, UInt256 balance, out UInt256 cumulativeCost)
            => tx.IsOverflowWhenAddingTxCostToCumulative(currentCost, out cumulativeCost) || balance < cumulativeCost;

        internal static bool IsOverflowWhenAddingTxCostToCumulative(this Transaction tx, UInt256 currentCost, out UInt256 cumulativeCost)
        {
            bool overflow = false;

            overflow |= UInt256.MultiplyOverflow(tx.MaxFeePerGas, (UInt256)tx.GasLimit, out UInt256 maxTxCost);
            overflow |= UInt256.AddOverflow(currentCost, maxTxCost, out cumulativeCost);
            overflow |= UInt256.AddOverflow(cumulativeCost, tx.Value, out cumulativeCost);

            if (tx.SupportsBlobs)
            {
                // if tx.SupportsBlobs and has BlobVersionedHashes = null, it will throw on earlier step of validation, in TxValidator
                overflow |= UInt256.MultiplyOverflow(Eip4844Constants.GasPerBlob, (UInt256)tx.BlobVersionedHashes!.Length, out UInt256 blobGas);
                overflow |= UInt256.MultiplyOverflow(blobGas, tx.MaxFeePerBlobGas ?? UInt256.MaxValue, out UInt256 blobGasCost);
                overflow |= UInt256.AddOverflow(cumulativeCost, blobGasCost, out cumulativeCost);
            }

            return overflow;
        }

        internal static bool IsOverflowInTxCostAndValue(this Transaction tx, out UInt256 txCost)
            => IsOverflowWhenAddingTxCostToCumulative(tx, UInt256.Zero, out txCost);

        public static bool IsInMempoolForm(this Transaction tx) => tx.NetworkWrapper is not null;

        public static ProofVersion GetProofVersion(this Transaction mempoolTx) => mempoolTx switch
        {
            LightTransaction lt => lt.ProofVersion,
            { NetworkWrapper: ShardBlobNetworkWrapper { Version: ProofVersion v } } => v,
            _ => ProofVersion.V0,
        };
    }
}
