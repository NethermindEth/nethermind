// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool.Collections;

[assembly: InternalsVisibleTo("Nethermind.TxPool.Test")]

namespace Nethermind.TxPool
{
    public static class TransactionExtensions
    {
        private static readonly long MaxSizeOfTxForBroadcast = 4.KiB; //4KB, as in Geth https://github.com/ethereum/go-ethereum/pull/27618
        private static readonly ITransactionSizeCalculator _transactionSizeCalculator = new NetworkTransactionSizeCalculator(TxDecoder.Instance);

        public static int GetLength(this Transaction tx, bool shouldCountBlobs = true) => tx.GetLength(_transactionSizeCalculator, shouldCountBlobs);

        public static bool CanPayBaseFee(this Transaction tx, UInt256 currentBaseFee) => (UInt256)tx.MaxFeePerGas >= currentBaseFee;

        public static bool CanPayForBlobGas(this Transaction tx, UInt256 currentPricePerBlobGas) => !tx.CarriesBlobs || tx.MaxFeePerBlobGas >= currentPricePerBlobGas;

        public static bool CanBeBroadcast(this Transaction tx) => !tx.CarriesBlobs && tx.GetLength() <= MaxSizeOfTxForBroadcast;

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

        /// <summary>Whether the sender's balance is what a pooled <paramref name="tx"/>'s gas and blob fees are
        /// measured against.</summary>
        /// <remarks>An EIP-8141 frame transaction that resolved a third-party payer is measured against that payer
        /// instead — by <see cref="Filters.FrameTxPayerExposureFilter"/> at admission, and against the same account
        /// by the revalidation sweep. Charging it to the sender would evict a sponsored transaction whose sponsor
        /// still covers it, and let it spend the sender's budget for its other transactions. One admitted with no
        /// payer resolved names no other account, so it stays on the sender. Only meaningful once admission has
        /// recorded a payer.</remarks>
        internal static bool FeeChargedToSender(this Transaction tx) =>
            !tx.SupportsFrames || tx.PayerAddress is null || tx.PayerAddress == tx.SenderAddress;

        internal static bool CheckForNotEnoughBalance(this Transaction tx, UInt256 currentCost, UInt256 balance, out UInt256 cumulativeCost)
            => tx.IsOverflowWhenAddingTxCostToCumulative(currentCost, out cumulativeCost) || balance < cumulativeCost;

        private struct SenderBucketState(UInt256 accountNonce, UInt256 txNonce, bool unreservedOnly)
        {
            public readonly UInt256 AccountNonce = accountNonce;
            public readonly UInt256 TxNonce = txNonce;
            public readonly bool UnreservedOnly = unreservedOnly;
            public UInt256 CumulativeCost = UInt256.Zero;
            public bool Overflow = false;
        }

        /// <summary>Sums what the sender of <paramref name="tx"/> already owes across the pending transactions
        /// ahead of it, so one balance cannot fund a whole bucket.</summary>
        /// <remarks>An EIP-8141 frame transaction a third-party payer covers is never counted — its cost is that
        /// payer's. <paramref name="unreservedOnly"/> additionally drops the self-paid ones, for a caller that
        /// measures against <see cref="PayerExposureCache"/>, which already sums them.</remarks>
        /// <returns><c>true</c> when the sum overflows, leaving <paramref name="cumulativeCost"/> unusable.</returns>
        internal static bool IsOverflowWhenSummingSenderBucket(this Transaction tx, TxDistinctSortedPool pool, in UInt256 accountNonce, bool unreservedOnly, out UInt256 cumulativeCost)
        {
            SenderBucketState bucket = new(accountNonce, tx.Nonce, unreservedOnly);
            // tx.SenderAddress! as unknownSenderFilter will run before either caller
            pool.VisitBucket(tx.SenderAddress!, ref bucket, static (Transaction otherTx, ref SenderBucketState bucketState) =>
            {
                if (otherTx.Nonce < bucketState.AccountNonce)
                {
                    return true;
                }

                if (otherTx.Nonce >= bucketState.TxNonce)
                {
                    return false;
                }

                bool chargedElsewhere = bucketState.UnreservedOnly
                    ? otherTx.PayerAddress is not null
                    : !otherTx.FeeChargedToSender();
                if (chargedElsewhere)
                {
                    return true;
                }

                bucketState.Overflow |= otherTx.IsOverflowWhenAddingPricedCostToCumulative(bucketState.CumulativeCost, out bucketState.CumulativeCost);
                return true;
            });

            cumulativeCost = bucket.CumulativeCost;
            return bucket.Overflow;
        }

        /// <summary>Adds what a pooled <paramref name="tx"/> costs the account that pays it, preferring the
        /// figure admission priced over the gas-limit product.</summary>
        /// <remarks>An EIP-8141 frame transaction is priced on its whole gas budget, of which
        /// <see cref="Transaction.GasLimit"/> carries only the frame-gas sum, so the product understates it.</remarks>
        private static bool IsOverflowWhenAddingPricedCostToCumulative(this Transaction tx, in UInt256 currentCost, out UInt256 cumulativeCost)
            => tx.PayerExposure is { } priced
                ? UInt256.AddOverflow(currentCost, priced, out cumulativeCost)
                : tx.IsOverflowWhenAddingTxCostToCumulative(currentCost, out cumulativeCost);

        internal static bool IsOverflowWhenAddingTxCostToCumulative(this Transaction tx, UInt256 currentCost, out UInt256 cumulativeCost)
        {
            bool overflow = false;

            overflow |= UInt256.MultiplyOverflow((UInt256)tx.MaxFeePerGas, tx.GasLimit, out UInt256 maxTxCost);
            overflow |= UInt256.AddOverflow(currentCost, maxTxCost, out cumulativeCost);
            overflow |= UInt256.AddOverflow(cumulativeCost, (UInt256)tx.Value, out cumulativeCost);

            // EIP-8141: blob fee priced at max_fee_per_blob_gas, an upper bound on the processor's escrow,
            // so mempool affordability never admits a tx the processor cannot charge.
            if (tx.CarriesBlobs)
            {
                overflow |= UInt256.MultiplyOverflow(Eip4844Constants.GasPerBlob, (UInt256)tx.GetBlobCount(), out UInt256 blobGas);
                overflow |= UInt256.MultiplyOverflow(blobGas, tx.MaxFeePerBlobGas ?? UInt256.MaxValue, out UInt256 blobGasCost);
                overflow |= UInt256.AddOverflow(cumulativeCost, blobGasCost, out cumulativeCost);
            }

            return overflow;
        }

        internal static bool IsOverflowInTxCostAndValue(this Transaction tx, out UInt256 txCost)
            => IsOverflowWhenAddingTxCostToCumulative(tx, UInt256.Zero, out txCost);

        public static bool IsInMempoolForm(this Transaction tx) => tx.NetworkWrapper is not null;
    }
}
