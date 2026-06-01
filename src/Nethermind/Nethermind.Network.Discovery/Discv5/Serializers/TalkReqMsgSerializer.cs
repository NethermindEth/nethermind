// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal sealed class TalkReqMsgSerializer : MsgSerializerBase
{
    public int GetContentLength(TalkReqMsg msg)
        => GetRequestIdLength(msg.RequestId) + Rlp.LengthOf(msg.Protocol) + Rlp.LengthOf(msg.Request);

    public void Serialize(Span<byte> buffer, ref int position, TalkReqMsg msg)
    {
        EncodeRequestId(buffer, ref position, msg.RequestId);
        position = Rlp.Encode(buffer, position, msg.Protocol);
        position = Rlp.Encode(buffer, position, msg.Request);
    }

    public TalkReqMsg Deserialize(RequestId requestId, ref Rlp.ValueDecoderContext ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner)
        => new(requestId, DecodeByteMemory(ref ctx, ownedMessage), DecodeByteMemory(ref ctx, ownedMessage), owner);
}
