// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class GetBlockBodiesMessageSerializer : IZeroInnerMessageSerializer<GetBlockBodiesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetBlockBodiesMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length, true);
            NettyRlpStream nettyRlpStream = new(byteBuffer);

            nettyRlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.BlockHashes.Count; i++)
            {
                nettyRlpStream.Encode(message.BlockHashes[i]);
            }
        }

        public GetBlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
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

        public static GetBlockBodiesMessage Deserialize(RlpStream rlpStream)
        {
            Keccak[] hashes = rlpStream.DecodeArray(ctx => rlpStream.DecodeKeccak(), false);
            return new GetBlockBodiesMessage(hashes);
        }
    }
}
