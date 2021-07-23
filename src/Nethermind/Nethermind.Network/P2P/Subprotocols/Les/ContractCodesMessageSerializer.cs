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
    public class ContractCodesMessageSerializer: IZeroMessageSerializer<ContractCodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, ContractCodesMessage message)
        {
            int innerLength = 0;
            for (int i = 0; i < message.Codes.Length; i++)
            {
                innerLength += Rlp.LengthOf(message.Codes[i]);
            }
            int contentLength =
                Rlp.LengthOf(message.RequestId) +
                Rlp.LengthOf(message.BufferValue) +
                Rlp.GetSequenceRlpLength(innerLength);

            int totalLength = Rlp.GetSequenceRlpLength(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(message.BufferValue);
            rlpStream.StartSequence(innerLength);
            for (int i = 0; i < message.Codes.Length; i++)
            {
                rlpStream.Encode(message.Codes[i]);
            }
        }

        public ContractCodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }

        public static ContractCodesMessage Deserialize(RlpStream rlpStream)
        {
            ContractCodesMessage contractCodesMessage = new ContractCodesMessage();
            rlpStream.ReadSequenceLength();
            contractCodesMessage.RequestId = rlpStream.DecodeLong();
            contractCodesMessage.BufferValue = rlpStream.DecodeInt();
            contractCodesMessage.Codes = rlpStream.DecodeArray(stream => stream.DecodeByteArray());
            return contractCodesMessage;
        }
    }
}
