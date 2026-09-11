// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal sealed class PingMsgSerializer() : MsgSerializerBase<PingMsg>(MessageType.Ping)
{
    protected override int GetContentLengthCore(PingMsg msg)
        => Rlp.LengthOf(msg.EnrSequence);

    protected override void SerializeCore<TWriter>(ref TWriter writer, PingMsg msg) => Encode(ref writer, msg.EnrSequence);

    protected override PingMsg DeserializeCore(in RequestId requestId, ref RlpReader ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner)
        => new(requestId, ctx.DecodeULong(), owner);
}
