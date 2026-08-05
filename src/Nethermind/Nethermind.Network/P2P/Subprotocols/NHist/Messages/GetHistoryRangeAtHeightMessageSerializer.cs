// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class GetHistoryRangeAtHeightMessageSerializer : NHistSerializerBase<GetHistoryRangeAtHeightMessage>
{
    protected override GetHistoryRangeAtHeightMessage Deserialize(ref RlpReader ctx)
    {
        GetHistoryRangeAtHeightMessage message = new();
        ctx.ReadSequenceLength();

        message.RequestId = ctx.DecodeLong();
        message.StartKey = ctx.DecodeValueKeccak() ?? default;
        message.EndKey = ctx.DecodeValueKeccak() ?? default;
        message.Height = ctx.DecodeULong();
        message.Cursor = ctx.DecodeByteArray(NHistMessageLimits.CursorRlpLimit);
        message.ResponseBytes = ctx.DecodeLong();

        return message;
    }

    public override void Serialize(IByteBuffer byteBuffer, GetHistoryRangeAtHeightMessage message)
    {
        ByteBufferRlpWriter writer = GetRlpWriterAndStartSequence(byteBuffer, message);

        writer.Encode(message.RequestId);
        writer.Encode(message.StartKey);
        writer.Encode(message.EndKey);
        writer.Encode(message.Height);
        writer.Encode(message.Cursor);
        writer.Encode(message.ResponseBytes);
    }

    public override int GetLength(GetHistoryRangeAtHeightMessage message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.RequestId);
        contentLength += Rlp.LengthOf((ValueHash256?)message.StartKey);
        contentLength += Rlp.LengthOf((ValueHash256?)message.EndKey);
        contentLength += Rlp.LengthOf(message.Height);
        contentLength += Rlp.LengthOf(message.Cursor);
        contentLength += Rlp.LengthOf(message.ResponseBytes);

        return Rlp.LengthOfSequence(contentLength);
    }
}
