// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class GetHistoryRowsMessageSerializer : NHistSerializerBase<GetHistoryRowsMessage>
{
    protected override GetHistoryRowsMessage Deserialize(ref RlpReader ctx)
    {
        GetHistoryRowsMessage message = new();
        ctx.ReadSequenceLength();

        message.RequestId = ctx.DecodeLong();
        message.Column = (HistoryRowColumn)ctx.DecodeByte();
        message.StartKey = ctx.DecodeByteArray(NHistMessageLimits.StartKeyRlpLimit);
        message.EndKey = ctx.DecodeByteArray(NHistMessageLimits.EndKeyRlpLimit);
        message.Cursor = ctx.DecodeByteArray(NHistMessageLimits.RowCursorRlpLimit);
        message.ResponseBytes = ctx.DecodeLong();

        return message;
    }

    public override void Serialize(IByteBuffer byteBuffer, GetHistoryRowsMessage message)
    {
        ByteBufferRlpWriter writer = GetRlpWriterAndStartSequence(byteBuffer, message);

        writer.Encode(message.RequestId);
        writer.Encode((byte)message.Column);
        writer.Encode(message.StartKey);
        writer.Encode(message.EndKey);
        writer.Encode(message.Cursor);
        writer.Encode(message.ResponseBytes);
    }

    public override int GetLength(GetHistoryRowsMessage message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.RequestId);
        contentLength += Rlp.LengthOf((byte)message.Column);
        contentLength += Rlp.LengthOf(message.StartKey);
        contentLength += Rlp.LengthOf(message.EndKey);
        contentLength += Rlp.LengthOf(message.Cursor);
        contentLength += Rlp.LengthOf(message.ResponseBytes);

        return Rlp.LengthOfSequence(contentLength);
    }
}
