// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public static class MessageSizeEstimator
    {
        public static ulong EstimateSize(TxReceipt[] receipts)
        {
            IRlpDecoder<TxReceipt> decoder = Rlp.GetDecoderOrThrow<TxReceipt>();
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
