// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Network.P2P.Subprotocols.Verkle.Messages;

public class GetSubTreeRangeMessageSerializer : VerkleMessageSerializerBase<GetSubTreeRangeMessage>
{
    public override void Serialize(IByteBuffer byteBuffer, GetSubTreeRangeMessage message)
    {
        NettyRlpStream rlpStream = GetRlpStreamAndStartSequence(byteBuffer, message);

        rlpStream.Encode(message.RequestId);
        rlpStream.Encode(message.SubTreeRange.RootHash.Bytes);
        rlpStream.Encode(message.SubTreeRange.StartingStem.Bytes);

        rlpStream.Encode(message.SubTreeRange.LimitStem?.Bytes ?? Stem.MaxValue.Bytes);
        rlpStream.Encode(message.ResponseBytes == 0 ? 1000_000 : message.ResponseBytes);
    }

    protected override GetSubTreeRangeMessage Deserialize(RlpStream rlpStream)
    {
        GetSubTreeRangeMessage message = new();
        rlpStream.ReadSequenceLength();

        message.RequestId = rlpStream.DecodeLong();
        message.SubTreeRange = new(new Hash256(rlpStream.DecodeByteArray()), rlpStream.DecodeByteArray(), rlpStream.DecodeByteArray());
        message.ResponseBytes = rlpStream.DecodeLong();

        return message;
    }

    public override int GetLength(GetSubTreeRangeMessage message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.RequestId);
        contentLength += Rlp.LengthOf(message.SubTreeRange.RootHash.Bytes);
        contentLength += Rlp.LengthOf(message.SubTreeRange.StartingStem.Bytes);
        contentLength += Rlp.LengthOf(message.SubTreeRange.LimitStem?.Bytes);
        contentLength += Rlp.LengthOf(message.ResponseBytes);

        return Rlp.LengthOfSequence(contentLength);
    }
}
