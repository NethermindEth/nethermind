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

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public class StatusMessageSerializer : IZeroMessageSerializer<StatusMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, StatusMessage message)
        {
            int forkIdContentLength = 0;
            int forkIdSequenceLength = 0;

            if (message.ForkId.HasValue)
            {
                ForkId forkId = message.ForkId.Value;
                forkIdContentLength = Rlp.LengthOf(forkId.ForkHash) + Rlp.LengthOf(forkId.Next);
                forkIdSequenceLength = Rlp.LengthOfSequence(forkIdContentLength);
            }

            NettyRlpStream rlpStream = new(byteBuffer);
            int contentLength =
                Rlp.LengthOf(message.ProtocolVersion) +
                Rlp.LengthOf(message.ChainId) +
                Rlp.LengthOf(message.TotalDifficulty) +
                Rlp.LengthOf(message.BestHash) +
                Rlp.LengthOf(message.GenesisHash) +
                forkIdSequenceLength;

            int totalLength = Rlp.LengthOfSequence(contentLength);
            byteBuffer.EnsureWritable(totalLength);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.ProtocolVersion);
            rlpStream.Encode(message.ChainId);
            rlpStream.Encode(message.TotalDifficulty);
            rlpStream.Encode(message.BestHash);
            rlpStream.Encode(message.GenesisHash);
            if (message.ForkId != null)
            {
                ForkId forkId = message.ForkId.Value;
                rlpStream.StartSequence(forkIdContentLength);
                rlpStream.Encode(forkId.ForkHash);
                rlpStream.Encode(forkId.Next);
            }
        }

        public StatusMessage Deserialize(IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }
        
        private static StatusMessage Deserialize(RlpStream rlpStream)
        {
            StatusMessage statusMessage = new StatusMessage();
            rlpStream.ReadSequenceLength();
            statusMessage.ProtocolVersion = rlpStream.DecodeByte();
            statusMessage.ChainId = rlpStream.DecodeUInt256();
            statusMessage.TotalDifficulty = rlpStream.DecodeUInt256();
            statusMessage.BestHash = rlpStream.DecodeKeccak();
            statusMessage.GenesisHash = rlpStream.DecodeKeccak();
            if (rlpStream.Position < rlpStream.Length)
            {
                rlpStream.ReadSequenceLength();
                byte[] forkHash = rlpStream.DecodeByteArray();
                long next = (long)rlpStream.DecodeUlong();
                ForkId forkId = new(forkHash, next);
                statusMessage.ForkId = forkId;
            }
            
            return statusMessage;
        }
    }
}
