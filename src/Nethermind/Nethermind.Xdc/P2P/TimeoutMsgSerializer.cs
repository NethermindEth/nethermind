// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc.P2P;

internal class TimeoutMsgSerializer : IZeroInnerMessageSerializer<TimeoutMsg>
{
    private static readonly TimeoutDecoder _timeDecoder = new TimeoutDecoder();

    public void Serialize(IByteBuffer byteBuffer, TimeoutMsg message)
    {
        int totalLength = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(totalLength);
        NettyRlpStream stream = new(byteBuffer);
        _timeDecoder.Encode(stream, message.Timeout);
    }

    public TimeoutMsg Deserialize(IByteBuffer byteBuffer)
    {
        Memory<byte> memory = byteBuffer.AsMemory();
        Rlp.ValueDecoderContext ctx = new(memory, true);
        Timeout timeout = _timeDecoder.Decode(ref ctx, RlpBehaviors.None);
        byteBuffer.SkipBytes(memory.Length);
        return new() { Timeout = timeout };
    }

    public int GetLength(TimeoutMsg message, out int contentLength)
    {
        contentLength = _timeDecoder.GetContentLength(message.Timeout, RlpBehaviors.None);
        return _timeDecoder.GetLength(message.Timeout, RlpBehaviors.None);
    }
}
