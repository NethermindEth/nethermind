// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth;

internal static class SoftLimitTestHelper
{
    private static readonly BlockDecoder BlockDecoder = new();

    public static int CountBlocksWithinSoftLimit(IReadOnlyList<Block> blocks)
    {
        ulong sizeEstimate = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            // Bodies are served from the stored blocks, so each counts with its encoded length.
            sizeEstimate += (ulong)BlockDecoder.GetLength(blocks[i], RlpBehaviors.None);
            if (sizeEstimate > SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit)
            {
                // The block crossing the soft limit is still served.
                return i + 1;
            }
        }

        return blocks.Count;
    }

    public static int CountReceiptBlocksWithinSoftLimit(TxReceipt[] receipts, int requestedCount)
    {
        ulong receiptBlockSize = MessageSizeEstimator.EstimateSize(receipts);
        ulong sizeEstimate = 0;
        for (int i = 0; i < requestedCount; i++)
        {
            // A block that does not fit under the hard cap is not served at all.
            if (sizeEstimate + receiptBlockSize > SyncPeerProtocolHandlerBase.HardOutgoingReceiptsMessageSizeLimit)
            {
                return i;
            }

            sizeEstimate += receiptBlockSize;
            if (sizeEstimate > SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit)
            {
                // The block crossing the soft limit is still served.
                return i + 1;
            }
        }

        return requestedCount;
    }
}
