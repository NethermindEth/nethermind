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

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class ByteCodesMessageSerializer : IZeroMessageSerializer<ByteCodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, ByteCodesMessage message)
        {
            (int contentLength, int codesLength) = GetLength(message);
            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength), true);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.StartSequence(codesLength);
            for (int i = 0; i < message.Codes.Length; i++)
            {
                rlpStream.Encode(message.Codes[i]);
            }
        }

        public ByteCodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);

            rlpStream.ReadSequenceLength();

            long requestId = rlpStream.DecodeLong();
            byte[][] result = rlpStream.DecodeArray(stream => stream.DecodeByteArray());

            return new ByteCodesMessage(result) { RequestId = requestId };
        }

        public (int contentLength, int codesLength) GetLength(ByteCodesMessage message)
        {
            int codesLength = 0;
            for (int i = 0; i < message.Codes.Length; i++)
            {
                codesLength += Rlp.LengthOf(message.Codes[i]);
            }
            
            return (codesLength + Rlp.LengthOf(message.RequestId), codesLength);
        }
    }
}
