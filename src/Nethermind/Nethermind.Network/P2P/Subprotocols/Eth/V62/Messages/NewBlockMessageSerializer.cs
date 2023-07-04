// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class NewBlockMessageSerializer : IZeroInnerMessageSerializer<NewBlockMessage>
    {
        private BlockDecoder _blockDecoder = new();

        public void Serialize(IByteBuffer byteBuffer, NewBlockMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length, true);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.Block);
            rlpStream.Encode(message.TotalDifficulty);
        }

        public NewBlockMessage Deserialize(IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }

        public int GetLength(NewBlockMessage message, out int contentLength)
        {
            contentLength = _blockDecoder.GetLength(message.Block, RlpBehaviors.None) +
                            Rlp.LengthOf(message.TotalDifficulty);

            return Rlp.LengthOfSequence(contentLength);
        }

        private static NewBlockMessage Deserialize(RlpStream rlpStream)
        {
            NewBlockMessage message = new();
            rlpStream.ReadSequenceLength();
            message.Block = Rlp.Decode<Block>(rlpStream);
            message.TotalDifficulty = rlpStream.DecodeUInt256();
            return message;
        }
    }
}
