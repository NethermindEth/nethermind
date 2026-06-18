// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Discovery.Serializers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal sealed class PongMsgSerializer : MsgSerializerBase<PongMsg>
{
    private static readonly RlpLimit IpAddressRlpLimit = RlpLimit.For<IPAddress>(16, nameof(PongMsg.RecipientIp));

    protected override int GetContentLengthCore(PongMsg msg)
        => Rlp.LengthOf(msg.EnrSequence) +
            IPAddressRlp.GetLength(msg.RecipientIp) +
            Rlp.LengthOf(msg.RecipientPort);

    protected override void SerializeCore(NettyRlpStream stream, PongMsg msg)
    {
        Encode(stream, msg.EnrSequence);
        IPAddressRlp.Encode(stream, msg.RecipientIp);
        Encode(stream, msg.RecipientPort);
    }

    protected override PongMsg DeserializeCore(in RequestId requestId, ref Rlp.ValueDecoderContext ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner)
    {
        ulong enrSequence = ctx.DecodeULong();
        IPAddress recipientIp = new(ctx.DecodeByteArraySpan(IpAddressRlpLimit));
        int recipientPort = ctx.DecodePositiveInt();
        return new PongMsg(requestId, enrSequence, recipientIp, recipientPort, owner);
    }
}
