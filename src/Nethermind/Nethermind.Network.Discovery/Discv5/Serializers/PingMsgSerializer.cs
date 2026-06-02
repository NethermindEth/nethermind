// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal sealed class PingMsgSerializer : MsgSerializerBase
{
    public int GetContentLength(PingMsg msg)
        => GetRequestIdLength(msg.RequestId) + Rlp.LengthOf(msg.EnrSequence);

    public void Serialize(NettyRlpStream stream, PingMsg msg)
    {
        EncodeRequestId(stream, msg.RequestId);
        Encode(stream, msg.EnrSequence);
    }

    public PingMsg Deserialize(RequestId requestId, ref Rlp.ValueDecoderContext ctx, ArrayPoolSpan<byte>? owner)
        => new(requestId, ctx.DecodeULong(), owner);
}
