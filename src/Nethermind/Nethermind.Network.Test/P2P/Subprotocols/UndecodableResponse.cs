// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols;

/// <summary>
/// A <c>[request-id, ...]</c> response whose request id reads fine but whose body cannot be decoded,
/// so a handler that decoded it before correlating would fail with an RLP error instead.
/// </summary>
internal static class UndecodableResponse
{
    public const long RequestId = 1111;

    public static IByteBuffer Create(long requestId = RequestId)
    {
        byte[] id = Rlp.Encode(requestId).Bytes;
        // A list header promising 5 bytes that never follow.
        const byte truncatedList = 0xc5;

        byte[] payload = new byte[2 + id.Length];
        payload[0] = (byte)(0xc0 + id.Length + 1);
        id.CopyTo(payload, 1);
        payload[^1] = truncatedList;
        return Unpooled.WrappedBuffer(payload);
    }

    public static void AssertRejectedAsUnrequested(Action<ZeroPacket> handle, int packetType)
    {
        ZeroPacket packet = new(Create()) { PacketType = (byte)packetType };
        try
        {
            Assert.That(() => handle(packet),
                Throws.TypeOf<SubprotocolException>().With.Message.Contains("has not been requested"));
        }
        finally
        {
            ReferenceCountUtil.Release(packet);
        }
    }
}
