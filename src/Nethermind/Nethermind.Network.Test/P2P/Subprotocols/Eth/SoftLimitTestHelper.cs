// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth;

internal static class SoftLimitTestHelper
{
    public static int CountBlocksWithinSoftLimit(IReadOnlyList<Block> blocks)
    {
        ulong sizeEstimate = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            sizeEstimate += MessageSizeEstimator.EstimateSize(blocks[i]);
            if (sizeEstimate > SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit)
            {
                return i;
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
            sizeEstimate += receiptBlockSize;
            if (sizeEstimate > SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit)
            {
                return i;
            }
        }

        return requestedCount;
    }
}
