// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

[assembly: InternalsVisibleTo("Nethermind.TxPool.Test")]

namespace Nethermind.TxPool
{
    public static class TransactionExtensions
    {
        private static readonly long MaxSizeOfTxForBroadcast = 4.KiB; //4KB, as in Geth https://github.com/ethereum/go-ethereum/pull/27618
        private static readonly ITransactionSizeCalculator _transactionSizeCalculator = new NetworkTransactionSizeCalculator(TxDecoder.Instance);

        public static int GetLength(this Transaction tx, bool shouldCountBlobs = true) => tx.GetLength(_transactionSizeCalculator, shouldCountBlobs);

        /// <summary>
        /// Size in bytes of the blob-elided typed transaction encoding of <paramref name="tx"/>, as announced in
        /// <c>NewPooledTransactionHashes</c> for eth/72 and measured by peers after decoding a
        /// <c>PooledTransactions</c> response.
        /// </summary>
        /// <remarks>
        /// The current devp2p text calls for the consensus encoding size, but established clients size-check the
        /// delivered encoding instead. See <see href="https://github.com/ethereum/devp2p/pull/281"/>.
        /// </remarks>
        public static int GetElidedNetworkEncodingSize(this Transaction tx)
        {
            if (tx is LightTransaction lightTx)
            {
                return lightTx.GetElidedNetworkEncodingSize();
            }

            if (!tx.SupportsBlobs)
            {
                return tx.GetLength();
            }

            if (tx.NetworkWrapper is not ShardBlobNetworkWrapper wrapper)
            {
                return 0;
            }

            int versionLength = GetProofVersionLength(wrapper.Version);
            if (versionLength < 0)
            {
                return 0;
            }

            int commitmentsLength = GetFixedByteStringsSequenceLength(wrapper.Commitments.Length, Ckzg.BytesPerCommitment);
            int proofsLength = GetFixedByteStringsSequenceLength(wrapper.Proofs.Length, Ckzg.BytesPerProof);
            if (commitmentsLength == 0 || proofsLength == 0)
            {
                return 0;
            }

            long contentLength = (long)tx.GetLength(shouldCountBlobs: false) - 1
                + versionLength
                + Rlp.OfEmptyList.Length
                + commitmentsLength
                + proofsLength;
            return GetTypedSequenceLength(contentLength);
        }

        internal static int CalculateElidedNetworkEncodingSize(int consensusEncodingSize, ProofVersion? proofVersion, int blobCount)
        {
            if (consensusEncodingSize <= 1 || blobCount <= 0 || proofVersion is null)
            {
                return 0;
            }

            int versionLength = GetProofVersionLength(proofVersion.Value);
            if (versionLength < 0)
            {
                return 0;
            }

            long proofCount = proofVersion is ProofVersion.V1
                ? (long)blobCount * Ckzg.CellsPerExtBlob
                : blobCount;
            int commitmentsLength = GetFixedByteStringsSequenceLength(blobCount, Ckzg.BytesPerCommitment);
            int proofsLength = GetFixedByteStringsSequenceLength(proofCount, Ckzg.BytesPerProof);
            if (commitmentsLength == 0 || proofsLength == 0)
            {
                return 0;
            }

            long contentLength = (long)consensusEncodingSize - 1
                + versionLength
                + Rlp.OfEmptyList.Length
                + commitmentsLength
                + proofsLength;
            return GetTypedSequenceLength(contentLength);
        }

        private static int GetFixedByteStringsSequenceLength(long count, int itemLength)
        {
            int encodedItemLength = Rlp.LengthOfByteString(itemLength, firstByte: 0);
            return count < 0 || count > long.MaxValue / encodedItemLength
                ? 0
                : GetSequenceLength(count * encodedItemLength);
        }

        private static int GetTypedSequenceLength(long contentLength)
        {
            int sequenceLength = GetSequenceLength(contentLength);
            return sequenceLength is > 0 and < int.MaxValue ? sequenceLength + 1 : 0;
        }

        private static int GetSequenceLength(long contentLength)
        {
            const int maxSequencePrefixLength = 1 + sizeof(int);
            if (contentLength is < 0 or > int.MaxValue - maxSequencePrefixLength)
            {
                return 0;
            }

            return Rlp.LengthOfSequence((int)contentLength);
        }

        private static int GetProofVersionLength(ProofVersion proofVersion) => proofVersion switch
        {
            ProofVersion.V0 => 0,
            ProofVersion.V1 => Rlp.LengthOf((byte)proofVersion),
            _ => -1,
        };

        public static bool CanPayBaseFee(this Transaction tx, UInt256 currentBaseFee) => (UInt256)tx.MaxFeePerGas >= currentBaseFee;

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

            overflow |= UInt256.MultiplyOverflow((UInt256)tx.MaxFeePerGas, tx.GasLimit, out UInt256 maxTxCost);
            overflow |= UInt256.AddOverflow(currentCost, maxTxCost, out cumulativeCost);
            overflow |= UInt256.AddOverflow(cumulativeCost, (UInt256)tx.Value, out cumulativeCost);

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
    }
}
