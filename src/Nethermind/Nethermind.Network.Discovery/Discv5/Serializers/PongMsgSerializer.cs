// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal sealed class PongMsgSerializer : MsgSerializerBase
{
    public int GetContentLength(PongMsg msg)
        => GetRequestIdLength(msg.RequestId) +
            Rlp.LengthOf(msg.EnrSequence) +
            GetAddressRlpLength(msg.RecipientIp) +
            Rlp.LengthOf(msg.RecipientPort);

    public void Serialize(Span<byte> buffer, ref int position, PongMsg msg)
    {
        EncodeRequestId(buffer, ref position, msg.RequestId);
        Encode(buffer, ref position, msg.EnrSequence);
        EncodeAddress(buffer, ref position, msg.RecipientIp);
        Encode(buffer, ref position, msg.RecipientPort);
    }

    public PongMsg Deserialize(RequestId requestId, ref Rlp.ValueDecoderContext ctx, ArrayPoolSpan<byte>? owner)
    {
        ulong enrSequence = ctx.DecodeULong();
        IPAddress recipientIp = new(ctx.DecodeByteArraySpan());
        int recipientPort = ctx.DecodePositiveInt();
        return new PongMsg(requestId, enrSequence, recipientIp, recipientPort, owner);
    }

    private static int GetAddressRlpLength(IPAddress ip)
    {
        if (ip.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return Rlp.LengthOfByteString(4, 0);
        }

        if (ip.AddressFamily is System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return Rlp.LengthOfByteString(16, 0);
        }

        return Rlp.LengthOf(ip.GetAddressBytes());
    }

    private static void EncodeAddress(Span<byte> buffer, ref int position, IPAddress ip)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (ip.TryWriteBytes(bytes, out int bytesWritten))
        {
            position = Rlp.Encode(buffer, position, bytes[..bytesWritten]);
            return;
        }

        position = Rlp.Encode(buffer, position, ip.GetAddressBytes());
    }
}
