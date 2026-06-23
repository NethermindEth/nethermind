// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal sealed class TalkReqMsgSerializer() : MsgSerializerBase<TalkReqMsg>(MessageType.TalkReq, requiresOwnedMemory: true)
{
    protected override int GetContentLengthCore(TalkReqMsg msg)
        => Rlp.LengthOf(msg.Protocol) + Rlp.LengthOf(msg.Request);

    protected override void SerializeCore(NettyRlpStream stream, TalkReqMsg msg)
    {
        stream.Encode(msg.Protocol);
        stream.Encode(msg.Request);
    }

    protected override TalkReqMsg DeserializeCore(in RequestId requestId, ref Rlp.ValueDecoderContext ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner)
        => new(requestId, DecodeByteMemory(ref ctx, ownedMessage), DecodeByteMemory(ref ctx, ownedMessage), owner);
}
