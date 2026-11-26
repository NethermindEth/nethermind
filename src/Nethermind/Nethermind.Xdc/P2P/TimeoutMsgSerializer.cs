// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.P2P;

internal class TimeoutMsgSerializer : IMessageSerializer<TimeoutMsg>
{
    private static readonly TimeoutDecoder _timeDecoder = new();

    public int Serialize(Span<byte> destination, TimeoutMsg message)
    {
        RlpWriter writer = RlpWriter.ContentWriter(destination);
        _timeDecoder.Encode(ref writer, message.Timeout);
        return writer.Length;
    }

    public TimeoutMsg Deserialize(ReadOnlySpan<byte> data)
    {
        RlpReader ctx = new(data);
        Timeout timeout = _timeDecoder.Decode(ref ctx, RlpBehaviors.None);
        ctx.CheckEnd();
        return new() { Timeout = timeout };
    }

    public int GetLength(TimeoutMsg message) => _timeDecoder.GetLength(message.Timeout, RlpBehaviors.None);
}
