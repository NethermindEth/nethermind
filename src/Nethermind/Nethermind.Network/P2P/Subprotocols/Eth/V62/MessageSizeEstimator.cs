// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public static class MessageSizeEstimator
    {
        private const ulong HeaderSizeEstimate = 512; // rough knowledge about the size of a header

        public static ulong EstimateSize(BlockHeader blockHeader)
        {
            if (blockHeader is null)
            {
                return 0;
            }

            return HeaderSizeEstimate;
        }

        /// <summary>Returns the size estimate of a block whose transactions sequence has a payload of <paramref name="transactionsLength"/> bytes.</summary>
        public static ulong EstimateBlockSize(int transactionsLength) => HeaderSizeEstimate + (ulong)transactionsLength;

        /// <summary>Returns the longest transactions payload whose block size estimate is at most <paramref name="sizeLimit"/>, or -1 when no block fits.</summary>
        public static int MaxTransactionsLength(ulong sizeLimit) =>
            sizeLimit < HeaderSizeEstimate ? -1 : (int)Math.Min(sizeLimit - HeaderSizeEstimate, int.MaxValue);

        public static ulong EstimateSize(Transaction tx)
        {
            if (tx is null)
            {
                return 0;
            }

            // Exact encoded length so access/authorization lists aren't under-counted.
            return (ulong)TxDecoder.Instance.GetLength(tx, RlpBehaviors.None);
        }

        public static ulong EstimateSize(Block? block)
        {
            if (block is null)
            {
                return 0;
            }

            ulong estimate = EstimateSize(block.Header);
            Transaction[] transactions = block.Transactions;
            for (int i = 0; i < transactions.Length; i++)
            {
                estimate += EstimateSize(transactions[i]);
            }

            return estimate;
        }

        public static ulong EstimateSize(TxReceipt[] receipts) =>
            EstimateSize(receipts, Rlp.GetDecoderOrThrow<TxReceipt>());

        /// <summary>Returns the encoded size of one block's receipts list in the wire format of <paramref name="decoder"/>.</summary>
        public static ulong EstimateSize(TxReceipt[] receipts, IRlpDecoder<TxReceipt> decoder)
        {
            ulong estimate = 0;

            for (int i = 0; i < receipts.Length; i++)
            {
                // Exact encoded length so log addresses and RLP framing aren't under-counted. Receipts
                // without a post-transaction state encode the same length under either EIP-658 behavior.
                estimate += (ulong)decoder.GetLength(receipts[i], RlpBehaviors.None);
            }

            // A block's receipts go on the wire inside their own sequence.
            return estimate > int.MaxValue ? estimate : (ulong)Rlp.LengthOfSequence((int)estimate);
        }
    }
}
