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
            ctx.ReadSequenceLength();
            requestId = ctx.DecodeLong();
            return true;
        }
        catch (RlpException)
        {
            return false;
        }
    }
}
