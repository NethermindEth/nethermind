// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Discovery.Serializers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal sealed class PongMsgSerializer : MsgSerializerBase
{
    public int GetContentLength(PongMsg msg)
        => GetRequestIdLength(msg.RequestId) +
            Rlp.LengthOf(msg.EnrSequence) +
            IPAddressRlp.GetLength(msg.RecipientIp) +
            Rlp.LengthOf(msg.RecipientPort);

    public void Serialize(NettyRlpStream stream, PongMsg msg)
    {
        EncodeRequestId(stream, msg.RequestId);
        Encode(stream, msg.EnrSequence);
        IPAddressRlp.Encode(stream, msg.RecipientIp);
        Encode(stream, msg.RecipientPort);
    }

    public PongMsg Deserialize(RequestId requestId, ref Rlp.ValueDecoderContext ctx, ArrayPoolSpan<byte>? owner)
    {
        ulong enrSequence = ctx.DecodeULong();
        IPAddress recipientIp = new(ctx.DecodeByteArraySpan());
        int recipientPort = ctx.DecodePositiveInt();
        return new PongMsg(requestId, enrSequence, recipientIp, recipientPort, owner);
    }

}
