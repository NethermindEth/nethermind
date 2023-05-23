// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockHeadersMessageSerializer : IZeroInnerMessageSerializer<BlockHeadersMessage>
    {
        private HeaderDecoder _headerDecoder = new();

        public void Serialize(IByteBuffer byteBuffer, BlockHeadersMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length, true);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);

            rlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.BlockHeaders.Length; i++)
            {
                rlpStream.Encode(message.BlockHeaders[i]);
            }
        }

        public BlockHeadersMessage Deserialize(IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }

        public int GetLength(BlockHeadersMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.BlockHeaders.Length; i++)
            {
                contentLength += _headerDecoder.GetLength(message.BlockHeaders[i], RlpBehaviors.None);
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        public static BlockHeadersMessage Deserialize(RlpStream rlpStream)
        {
            BlockHeadersMessage message = new();
            message.BlockHeaders = Rlp.DecodeArray<BlockHeader>(rlpStream);
            return message;
        }
    }
}
