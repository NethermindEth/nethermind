// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66;

internal static class Eth66RequestId
{
    /// <summary>
    /// Reads the request id that leads an eth/66-style <c>[request-id, ...]</c> message without decoding the rest.
    /// </summary>
    /// <returns><see langword="false"/> when the payload does not start with a list holding a request id.</returns>
    public static bool TryPeek(IByteBuffer content, out long requestId)
    {
        requestId = 0;
        RlpReader ctx = new(content.AsSpan());
        try
        {
            if (!NextItemFits(ref ctx, ctx.Length))
            {
                return false;
            }

            int end = ctx.ReadSequenceLength() + ctx.Position;
            if (!NextItemFits(ref ctx, end))
            {
                return false;
            }

            requestId = ctx.DecodeLong();
            return true;
        }
        catch (RlpException)
        {
            return false;
        }
    }

    // The RLP readers index prefix and content bytes without bounds checks, so a truncated
    // payload would throw IndexOutOfRangeException instead of being reported as unreadable.
    private static bool NextItemFits(ref RlpReader ctx, int end) =>
        ctx.Position < end && ctx.Position + ctx.PeekNextRlpLength() <= end;
}
