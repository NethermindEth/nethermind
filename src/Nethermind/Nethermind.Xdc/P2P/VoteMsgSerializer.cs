// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc.P2P;

internal class VoteMsgSerializer : IZeroInnerMessageSerializer<VoteMsg>
{
    private static readonly VoteDecoder _voteDecoder = new VoteDecoder();

    public void Serialize(IByteBuffer byteBuffer, VoteMsg message)
    {
        int totalLength = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(totalLength);
        NettyRlpStream stream = new(byteBuffer);
        _voteDecoder.Encode(stream, message.Vote);
    }

    public VoteMsg Deserialize(IByteBuffer byteBuffer)
    {
        Memory<byte> memory = byteBuffer.AsMemory();
        Rlp.ValueDecoderContext ctx = new(memory, true);
        Types.Vote vote = _voteDecoder.Decode(ref ctx, RlpBehaviors.None);
        byteBuffer.SkipBytes(memory.Length);
        return new() { Vote = vote };
    }

    public int GetLength(VoteMsg message, out int contentLength)
    {
        contentLength = _voteDecoder.GetContentLength(message.Vote, RlpBehaviors.None);
        return _voteDecoder.GetLength(message.Vote, RlpBehaviors.None);
    }
}
