// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.P2P;

internal class VoteMsgSerializer : IZeroInnerMessageSerializer<VoteMsg>
{
    private static readonly VoteDecoder _voteDecoder = new();

    public void Serialize(IByteBuffer byteBuffer, VoteMsg message) => this.SerializeWithLengths(byteBuffer, message);

    public void Serialize(IByteBuffer byteBuffer, VoteMsg message, ref LengthStack lengths)
    {
        ByteBufferRlpWriter writer = new(byteBuffer);
        _voteDecoder.Encode(ref writer, message.Vote, ref lengths);
    }

    public VoteMsg Deserialize(IByteBuffer byteBuffer)
    {
        RlpReader ctx = new(byteBuffer.AsSpan());
        Types.Vote vote = _voteDecoder.Decode(ref ctx, RlpBehaviors.None);
        byteBuffer.SkipBytes(ctx.Position);
        return new() { Vote = vote };
    }

    public void PopulateLength(VoteMsg message, ref LengthStack lengths) =>
        _voteDecoder.PopulateLength(message.Vote, ref lengths);
}
