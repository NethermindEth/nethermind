// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class AnnounceMessageSerializer : IZeroMessageSerializer<AnnounceMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, AnnounceMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            NettyRlpStream rlpStream = new(byteBuffer);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.HeadHash);
            rlpStream.Encode(message.HeadBlockNo);
            rlpStream.Encode(message.TotalDifficulty);
            rlpStream.Encode(message.ReorgDepth);
            rlpStream.Encode(Rlp.OfEmptySequence);
        }

        public AnnounceMessage Deserialize(IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }

        private int GetLength(AnnounceMessage message, out int contentLength)
        {
            contentLength =
                Rlp.LengthOf(message.HeadHash) +
                Rlp.LengthOf(message.HeadBlockNo) +
                Rlp.LengthOf(message.TotalDifficulty) +
                Rlp.LengthOf(message.ReorgDepth) +
                Rlp.OfEmptySequence.Length;

            return Rlp.LengthOfSequence(contentLength);
        }

        private static AnnounceMessage Deserialize(RlpStream rlpStream)
        {
            AnnounceMessage announceMessage = new();
            rlpStream.ReadSequenceLength();
            announceMessage.HeadHash = rlpStream.DecodeKeccak();
            announceMessage.HeadBlockNo = rlpStream.DecodeLong();
            announceMessage.TotalDifficulty = rlpStream.DecodeUInt256();
            announceMessage.ReorgDepth = rlpStream.DecodeLong();
            rlpStream.ReadSequenceLength();
            return announceMessage;
        }
    }
}
