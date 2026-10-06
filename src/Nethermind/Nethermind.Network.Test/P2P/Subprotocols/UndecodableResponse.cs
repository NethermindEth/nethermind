// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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

    public static byte[] Create(long requestId = RequestId)
    {
        byte[] id = Rlp.Encode(requestId).Bytes;
        // A list header promising 5 bytes that never follow.
        const byte truncatedList = 0xc5;

        byte[] payload = new byte[2 + id.Length];
        payload[0] = (byte)(0xc0 + id.Length + 1);
        id.CopyTo(payload, 1);
        payload[^1] = truncatedList;
        return payload;
    }

    public static void AssertRejectedAsUnrequested(Action<ZeroPacket> handle, int packetType) =>
        AssertRejected(handle, new ZeroPacket(Unpooled.WrappedBuffer(Create())) { PacketType = (byte)packetType }, "has not been requested");

    /// <summary>
    /// A <c>[request-id, ...fields, [[item, ...]]]</c> receipts response with one block of <paramref name="receipts"/>
    /// items that are not receipts, so decoding it would fail with an RLP error.
    /// </summary>
    /// <param name="requestId">The request id, or <see langword="null"/> for an eth/63 response, which is the block list alone.</param>
    public static byte[] CreateReceipts(long? requestId, int receipts, params Rlp[] fieldsBeforeReceipts)
    {
        Rlp block = Rlp.Encode(Enumerable.Repeat(new Rlp([0x01]), receipts).ToArray());
        Rlp blocks = Rlp.Encode(new[] { block });
        Rlp response = requestId is long id ? Rlp.Encode([Rlp.Encode(id), .. fieldsBeforeReceipts, blocks]) : blocks;
        return response.Bytes;
    }

    /// <summary>
    /// Asserts that a receipts response with more receipts than the request allows is rejected before it is decoded.
    /// </summary>
    public static void AssertReceiptsRejectedBeforeDecoding(Action<ZeroPacket> handle, byte[] content, int packetType) =>
        AssertRejected(handle, new ZeroPacket(Unpooled.WrappedBuffer(content)) { PacketType = (byte)packetType }, "exceeds the request");

    private static void AssertRejected(Action<ZeroPacket> handle, ZeroPacket packet, string message)
    {
        try
        {
            Assert.That(() => handle(packet), Throws.TypeOf<SubprotocolException>().With.Message.Contains(message));
        }
        finally
        {
            ReferenceCountUtil.Release(packet);
        }
    }
}
