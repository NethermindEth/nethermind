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

    public void Serialize(IByteBuffer byteBuffer, TimeoutMsg message) =>
        byteBuffer.WriteRlp(_timeDecoder, message.Timeout);

    public TimeoutMsg Deserialize(IByteBuffer byteBuffer)
    {
        Timeout timeout = byteBuffer.DeserializeRlp(_timeDecoder);
        return new() { Timeout = timeout };
    }

    public int GetLength(TimeoutMsg message, out int contentLength)
    {
        contentLength = _timeDecoder.GetContentLength(message.Timeout, RlpBehaviors.None);
        return _timeDecoder.GetLength(message.Timeout, RlpBehaviors.None);
    }
}
