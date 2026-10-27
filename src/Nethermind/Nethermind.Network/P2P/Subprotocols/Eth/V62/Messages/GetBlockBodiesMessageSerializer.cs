// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class GetBlockBodiesMessageSerializer : IZeroInnerMessageSerializer<GetBlockBodiesMessage>
    {
        private const int MaxBodyHashesPerRequest = 1024;
        private static readonly RlpLimit RlpLimit = RlpLimit.For<GetBlockBodiesMessage>(MaxBodyHashesPerRequest, nameof(GetBlockBodiesMessage.BlockHashes));

        public void Serialize(IByteBuffer byteBuffer, GetBlockBodiesMessage message) => this.SerializeWithLengths(byteBuffer, message);

        public void Serialize(IByteBuffer byteBuffer, GetBlockBodiesMessage message, ref LengthStack lengths)
        {
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(lengths.Pop());
            for (int i = 0; i < message.BlockHashes.Count; i++)
            {
                writer.Encode(message.BlockHashes[i]);
            }
        }

        public GetBlockBodiesMessage Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(Deserialize);

        public int PopulateLength(GetBlockBodiesMessage message, ref LengthStack lengths)
        {
            int contentLength = 0;
            for (int i = 0; i < message.BlockHashes.Count; i++)
            {
                contentLength += Rlp.LengthOf(message.BlockHashes[i]);
            }

            lengths.Push(contentLength);
            return Rlp.LengthOfSequence(contentLength);
        }

        public static GetBlockBodiesMessage Deserialize(ref RlpReader ctx)
        {
            Hash256[] hashes = ctx.DecodeNonNullArray(static (ref RlpReader c) => c.DecodeKeccak(), false, limit: RlpLimit);
            return new GetBlockBodiesMessage(hashes);
        }
    }
}
