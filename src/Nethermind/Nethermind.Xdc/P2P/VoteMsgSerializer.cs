// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.P2P;

internal class VoteMsgSerializer : IMessageSerializer<VoteMsg>
{
    private static readonly VoteDecoder _voteDecoder = new();

    public int Serialize(Span<byte> destination, VoteMsg message)
    {
        RlpWriter writer = RlpWriter.ContentWriter(destination);
        _voteDecoder.Encode(ref writer, message.Vote);
        return writer.Length;
    }

    public VoteMsg Deserialize(ReadOnlySpan<byte> data)
    {
        RlpReader ctx = new(data);
        Types.Vote vote = _voteDecoder.Decode(ref ctx, RlpBehaviors.None);
        ctx.CheckEnd();
        return new() { Vote = vote };
    }

    public int GetLength(VoteMsg message) => _voteDecoder.GetLength(message.Vote, RlpBehaviors.None);
}
