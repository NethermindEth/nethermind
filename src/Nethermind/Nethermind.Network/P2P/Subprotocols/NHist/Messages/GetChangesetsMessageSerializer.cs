// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class GetChangesetsMessageSerializer : NHistSerializerBase<GetChangesetsMessage>
{
    protected override GetChangesetsMessage Deserialize(ref RlpReader ctx)
    {
        GetChangesetsMessage message = new();
        ctx.ReadSequenceLength();

        message.RequestId = ctx.DecodeLong();
        message.FromBlock = ctx.DecodeULong();
        message.ToBlock = ctx.DecodeULong();
        message.ResponseBytes = ctx.DecodeLong();

        return message;
    }

    public override void Serialize(IByteBuffer byteBuffer, GetChangesetsMessage message)
    {
        ByteBufferRlpWriter writer = GetRlpWriterAndStartSequence(byteBuffer, message);

        writer.Encode(message.RequestId);
        writer.Encode(message.FromBlock);
        writer.Encode(message.ToBlock);
        writer.Encode(message.ResponseBytes);
    }

    public override int GetLength(GetChangesetsMessage message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.RequestId);
        contentLength += Rlp.LengthOf(message.FromBlock);
        contentLength += Rlp.LengthOf(message.ToBlock);
        contentLength += Rlp.LengthOf(message.ResponseBytes);

        return Rlp.LengthOfSequence(contentLength);
    }
}
