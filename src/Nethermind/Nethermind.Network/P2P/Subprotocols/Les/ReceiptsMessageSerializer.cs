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

using System;
using DotNetty.Buffers;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class ReceiptsMessageSerializer : IZeroMessageSerializer<ReceiptsMessage>
    {
        private readonly ISpecProvider _specProvider;

        public ReceiptsMessageSerializer(ISpecProvider specProvider)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }

        public void Serialize(IByteBuffer byteBuffer, ReceiptsMessage message)
        {
            Eth.V63.ReceiptsMessageSerializer ethSerializer = new Eth.V63.ReceiptsMessageSerializer(_specProvider);
            Rlp ethMessage = new Rlp(ethSerializer.Serialize(message.EthMessage));
            int contentLength = Rlp.LengthOf(message.RequestId) + Rlp.LengthOf(message.BufferValue) + ethMessage.Length;

            int totalLength = Rlp.GetSequenceRlpLength(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(message.BufferValue);
            rlpStream.Encode(ethMessage);
        }

        public ReceiptsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }

        public ReceiptsMessage Deserialize(RlpStream rlpStream)
        {
            ReceiptsMessage receiptsMessage = new ReceiptsMessage();
            Eth.V63.ReceiptsMessageSerializer ethSerializer = new Eth.V63.ReceiptsMessageSerializer(_specProvider);

            rlpStream.ReadSequenceLength();
            receiptsMessage.RequestId = rlpStream.DecodeLong();
            receiptsMessage.BufferValue = rlpStream.DecodeInt();
            receiptsMessage.EthMessage = ethSerializer.Deserialize(rlpStream);
            return receiptsMessage;
        }
    }
}
