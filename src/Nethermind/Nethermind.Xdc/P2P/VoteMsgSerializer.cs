// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.P2P;

internal class VoteMsgSerializer : IZeroMessageSerializer<VoteMsg>
{
    private static readonly VoteDecoder _voteDecoder = new();

    public void Serialize(Span<byte> buffer, VoteMsg message)
    {
        RlpWriter writer = new(buffer);
        _voteDecoder.Encode(ref writer, message.Vote);
    }

    public VoteMsg Deserialize(ReadOnlySpan<byte> data, out int consumed)
    {
        RlpReader ctx = new(data);
        Types.Vote vote = _voteDecoder.Decode(ref ctx, RlpBehaviors.None);
        consumed = ctx.Position;
        return new() { Vote = vote };
    }

    public int GetLength(VoteMsg message, out int contentLength)
    {
        contentLength = _voteDecoder.GetContentLength(message.Vote, RlpBehaviors.None);
        return _voteDecoder.GetLength(message.Vote, RlpBehaviors.None);
    }
}
