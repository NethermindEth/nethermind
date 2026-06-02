// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal sealed class TalkRespMsgSerializer : MsgSerializerBase
{
    public int GetContentLength(TalkRespMsg msg)
        => GetRequestIdLength(msg.RequestId) + Rlp.LengthOf(msg.Response);

    public void Serialize(NettyRlpStream stream, TalkRespMsg msg)
    {
        EncodeRequestId(stream, msg.RequestId);
        stream.Encode(msg.Response);
    }

    public TalkRespMsg Deserialize(RequestId requestId, ref Rlp.ValueDecoderContext ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner)
        => new(requestId, DecodeByteMemory(ref ctx, ownedMessage), owner);
}
