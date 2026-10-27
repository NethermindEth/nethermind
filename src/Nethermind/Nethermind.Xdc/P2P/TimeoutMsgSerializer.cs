// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.P2P;

internal class TimeoutMsgSerializer : IZeroInnerMessageSerializer<TimeoutMsg>
{
    private static readonly TimeoutDecoder _timeDecoder = new();

    public void Serialize(IByteBuffer byteBuffer, TimeoutMsg message) => this.SerializeWithLengths(byteBuffer, message);

    public void Serialize(IByteBuffer byteBuffer, TimeoutMsg message, ref LengthStack lengths)
    {
        ByteBufferRlpWriter writer = new(byteBuffer);
        _timeDecoder.Encode(ref writer, message.Timeout, ref lengths);
    }

    public TimeoutMsg Deserialize(IByteBuffer byteBuffer)
    {
        RlpReader ctx = new(byteBuffer.AsSpan());
        Timeout timeout = _timeDecoder.Decode(ref ctx, RlpBehaviors.None);
        byteBuffer.SkipBytes(ctx.Position);
        return new() { Timeout = timeout };
    }

    public void PopulateLength(TimeoutMsg message, ref LengthStack lengths) =>
        _timeDecoder.PopulateLength(message.Timeout, ref lengths);
}
