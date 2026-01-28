// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class NewBlockMessageSerializer : IZeroInnerMessageSerializer<NewBlockMessage>
    {
        /// <summary>
        /// Maximum total RLP elements allowed in a new block message.
        /// Single block with transactions, access lists, etc.
        /// </summary>
        private const int MaxTotalElements = 100_000;

        private readonly BlockDecoder _blockDecoder = new();

        public void Serialize(IByteBuffer byteBuffer, NewBlockMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.Block);
            rlpStream.Encode(message.TotalDifficulty);
        }

        public NewBlockMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);

            // Pass 1: Validate nested structure to prevent memory DOS
            RlpElementCounter.CountElementsInSequence(rlpStream, MaxTotalElements);

            // Pass 2: Actual decode (limits validated, safe to allocate)
            rlpStream.Position = 0;
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
