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
// 

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66
{
    public class Eth66MessageSerializer<TEth66Message, TEthMessage> : IZeroInnerMessageSerializer<TEth66Message>
        where TEth66Message : Eth66Message<TEthMessage>, new()
        where TEthMessage : P2PMessage
    {
        private readonly IZeroInnerMessageSerializer<TEthMessage> _ethMessageSerializer;

        protected Eth66MessageSerializer(IZeroInnerMessageSerializer<TEthMessage> ethMessageSerializer)
        {
            _ethMessageSerializer = ethMessageSerializer;
        }
        
        public void Serialize(IByteBuffer byteBuffer, TEth66Message message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length, true);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            _ethMessageSerializer.Serialize(byteBuffer, message.EthMessage);
        }

        public TEth66Message Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            TEth66Message eth66Message = new();
            rlpStream.ReadSequenceLength();
            eth66Message.RequestId = rlpStream.DecodeLong();
            eth66Message.EthMessage = _ethMessageSerializer.Deserialize(byteBuffer);
            return eth66Message;
        }

        public int GetLength(TEth66Message message, out int contentLength)
        {
            int innerMessageLength = _ethMessageSerializer.GetLength(message.EthMessage, out _);
            contentLength =
                Rlp.LengthOf(message.RequestId) +
                innerMessageLength;

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
