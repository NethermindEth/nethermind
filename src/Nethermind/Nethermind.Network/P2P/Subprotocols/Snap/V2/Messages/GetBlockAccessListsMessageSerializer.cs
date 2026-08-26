// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages
{
    public class GetBlockAccessListsMessageSerializer : SnapSerializerBase<GetBlockAccessListsMessage>
    {
        public override void Serialize(IByteBuffer byteBuffer, GetBlockAccessListsMessage message)
        {
            ByteBufferRlpWriter writer = GetRlpWriterAndStartSequence(byteBuffer, message);

            writer.Encode(message.RequestId);
            writer.Encode(message.BlockHashes);
            writer.Encode(message.Bytes);
        }

        protected override GetBlockAccessListsMessage Deserialize(ref RlpReader ctx)
        {
            GetBlockAccessListsMessage message = new();
            ctx.ReadSequenceLength();

            message.RequestId = ctx.DecodeLong();
            message.BlockHashes = ctx.DecodeArrayPoolList(static (ref RlpReader c) => c.DecodeValueKeccak() ?? default, limit: SnapMessageLimits.GetBlockAccessListsHashesRlpLimit);
            message.Bytes = ctx.DecodeLong();

            return message;
        }

        public override int GetLength(GetBlockAccessListsMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.BlockHashes, true);
            contentLength += Rlp.LengthOf(message.Bytes);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
