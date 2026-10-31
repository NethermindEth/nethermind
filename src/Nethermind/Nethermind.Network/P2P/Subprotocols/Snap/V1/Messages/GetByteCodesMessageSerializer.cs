// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class GetByteCodesMessageSerializer : SnapSerializerBase<GetByteCodesMessage>
    {
        public override void Serialize(IByteBuffer byteBuffer, GetByteCodesMessage message)
        {
            RlpWriter writer = GetRlpWriterAndStartSequence(byteBuffer, message, out RlpSequence sequence, out int totalLength);

            writer.Encode(message.RequestId);
            writer.Encode(message.Hashes);
            writer.Encode(message.Bytes);

            EndSequenceAndCompleteWrite(byteBuffer, ref writer, sequence, totalLength);
        }

        protected override GetByteCodesMessage Deserialize(ref RlpReader reader)
        {
            GetByteCodesMessage message = new();
            RlpReader ctx = reader.EnterSequence(out int itemLength);

            message.RequestId = ctx.DecodeLong();
            message.Hashes = ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) => c.DecodeValueKeccakNonNull(), limit: SnapMessageLimits.GetByteCodesHashesRlpLimit);
            message.Bytes = ctx.DecodeLong();

            reader.ExitSequenceIgnoringRest(ctx, itemLength);
            return message;
        }

        public override int GetLength(GetByteCodesMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.Hashes, true);
            contentLength += Rlp.LengthOf(message.Bytes);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
