// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.P2P;

internal class TimeoutMsgSerializer : IZeroMessageSerializer<TimeoutMsg>
{
    private static readonly TimeoutDecoder _timeDecoder = new();

    public void Serialize(Span<byte> buffer, TimeoutMsg message)
    {
        RlpWriter writer = new(buffer);
        _timeDecoder.Encode(ref writer, message.Timeout);
    }

    public TimeoutMsg Deserialize(ReadOnlySpan<byte> data, out int consumed)
    {
        RlpReader ctx = new(data);
        Timeout timeout = _timeDecoder.Decode(ref ctx, RlpBehaviors.None);
        consumed = ctx.Position;
        return new() { Timeout = timeout };
    }

    public int GetLength(TimeoutMsg message, out int contentLength)
    {
        contentLength = _timeDecoder.GetContentLength(message.Timeout, RlpBehaviors.None);
        return _timeDecoder.GetLength(message.Timeout, RlpBehaviors.None);
    }
}
