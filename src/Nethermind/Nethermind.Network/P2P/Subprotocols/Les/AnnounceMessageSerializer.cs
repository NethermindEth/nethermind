//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class AnnounceMessageSerializer : IZeroMessageSerializer<AnnounceMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, AnnounceMessage message)
        {
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);

            int contentLength =
                Rlp.LengthOf(message.HeadHash) +
                Rlp.LengthOf(message.HeadBlockNo) +
                Rlp.LengthOf(message.TotalDifficulty) +
                Rlp.LengthOf(message.ReorgDepth) + 
                Rlp.OfEmptySequence.Length;

            int totalLength = Rlp.LengthOfSequence(contentLength);
            byteBuffer.EnsureWritable(totalLength);
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

        private static AnnounceMessage Deserialize(RlpStream rlpStream)
        {
            AnnounceMessage announceMessage = new AnnounceMessage();
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
