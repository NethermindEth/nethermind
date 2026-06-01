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

    public void Serialize(Span<byte> buffer, ref int position, PingMsg msg)
    {
        EncodeRequestId(buffer, ref position, msg.RequestId);
        Encode(buffer, ref position, msg.EnrSequence);
    }

    public PingMsg Deserialize(RequestId requestId, ref Rlp.ValueDecoderContext ctx, ArrayPoolSpan<byte>? owner)
        => new(requestId, ctx.DecodeULong(), owner);
}
