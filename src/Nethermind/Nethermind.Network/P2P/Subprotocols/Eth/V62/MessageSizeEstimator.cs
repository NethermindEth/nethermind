// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public static class MessageSizeEstimator
    {
        public static ulong EstimateSize(BlockHeader blockHeader)
        {
            if (blockHeader is null)
            {
                return 0;
            }

            return 512; // rough knowledge about the size of a header
        }

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

        public static ulong EstimateSize(TxReceipt[] receipts)
        {
            IRlpDecoder<TxReceipt> decoder = Rlp.GetDecoderOrThrow<TxReceipt>();
            ulong estimate = 0;

            for (int i = 0; i < receipts.Length; i++)
            {
                estimate += EstimateSize(receipts[i], decoder);
            }

            // A block's receipts go on the wire inside their own sequence.
            return estimate > int.MaxValue ? estimate : (ulong)Rlp.LengthOfSequence((int)estimate);
        }

        private static ulong EstimateSize(TxReceipt? receipt, IRlpDecoder<TxReceipt> decoder)
        {
            if (receipt is null)
            {
                return 0;
            }

            // Exact encoded length so log addresses and RLP framing aren't under-counted. Receipts without
            // a post-transaction state encode the same length under either EIP-658 behavior.
            return (ulong)decoder.GetLength(receipt, RlpBehaviors.None);
        }
    }
}
