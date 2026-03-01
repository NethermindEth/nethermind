// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class GetBlockBodiesMessageSerializer : IZeroInnerMessageSerializer<GetBlockBodiesMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<GetBlockBodiesMessage>(NethermindSyncLimits.MaxBodyFetch, nameof(GetBlockBodiesMessage.BlockHashes));

        public void Serialize(IByteBuffer byteBuffer, GetBlockBodiesMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            NettyRlpStream nettyRlpStream = new(byteBuffer);

            nettyRlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.BlockHashes.Count; i++)
            {
                nettyRlpStream.Encode(message.BlockHashes[i]);
            }
        }

        public GetBlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            Rlp.ValueDecoderContext ctx = byteBuffer.AsRlpContext();
            GetBlockBodiesMessage message = Deserialize(ref ctx);
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
            return message;
        }

        public int GetLength(GetBlockBodiesMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.BlockHashes.Count; i++)
            {
                contentLength += Rlp.LengthOf(message.BlockHashes[i]);
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        public static GetBlockBodiesMessage Deserialize(ref Rlp.ValueDecoderContext ctx)
        {
            Hash256[] hashes = ctx.DecodeArray(static (ref Rlp.ValueDecoderContext c) => c.DecodeKeccak(), false, limit: RlpLimit);
            return new GetBlockBodiesMessage(hashes);
        }
    }
}
