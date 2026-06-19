// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal sealed class TalkRespMsgSerializer() : MsgSerializerBase<TalkRespMsg>(MessageType.TalkResp, requiresOwnedMemory: true)
{
    protected override int GetContentLengthCore(TalkRespMsg msg)
        => Rlp.LengthOf(msg.Response);

    protected override void SerializeCore(NettyRlpStream stream, TalkRespMsg msg)
        => stream.Encode(msg.Response);

    protected override TalkRespMsg DeserializeCore(in RequestId requestId, ref Rlp.ValueDecoderContext ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner)
        => new(requestId, DecodeByteMemory(ref ctx, ownedMessage), owner);
}
