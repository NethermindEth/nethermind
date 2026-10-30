// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class GetByteCodesMessageSerializer : SnapSerializerBase<GetByteCodesMessage>
    {
        public override void Serialize(IByteBuffer byteBuffer, GetByteCodesMessage message, ref LengthList lengths)
        {
            ByteBufferRlpWriter writer = GetRlpWriterAndStartSequence(byteBuffer, ref lengths);

            writer.Encode(message.RequestId);
            writer.Encode(message.Hashes);
            writer.Encode(message.Bytes);
        }

        protected override GetByteCodesMessage Deserialize(ref RlpReader ctx)
        {
            GetByteCodesMessage message = new();
            ctx.ReadSequenceLength();

            message.RequestId = ctx.DecodeLong();
            message.Hashes = ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) => c.DecodeValueKeccakNonNull(), limit: SnapMessageLimits.GetByteCodesHashesRlpLimit);
            message.Bytes = ctx.DecodeLong();

            return message;
        }

        public override void PopulateLength(GetByteCodesMessage message, in LengthListWriter lengths)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.Hashes, true);
            contentLength += Rlp.LengthOf(message.Bytes);

            using LengthListWriter sequence = lengths.BeginContainer();
            sequence.Add(contentLength);
        }
    }
}
