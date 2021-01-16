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
    public class GetHelperTrieProofsMessageSerializer: IZeroMessageSerializer<GetHelperTrieProofsMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetHelperTrieProofsMessage message)
        {
            int innerLength = 0;
            foreach (var request in message.Requests)
            {
                innerLength += Rlp.GetSequenceRlpLength(GetRequestLength(request));
            }
            int contentLength = Rlp.LengthOf(message.RequestId) + 
                Rlp.GetSequenceRlpLength(innerLength);

            int totalLength = Rlp.GetSequenceRlpLength(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.StartSequence(innerLength);
            foreach (var request in message.Requests)
            {
                rlpStream.StartSequence(GetRequestLength(request));
                rlpStream.Encode((int)request.SubType);
                rlpStream.Encode(request.SectionIndex);
                rlpStream.Encode(request.Key);
                rlpStream.Encode(request.FromLevel);
                rlpStream.Encode(request.AuxiliaryData);
            }
        }

        private int GetRequestLength(HelperTrieRequest request)
        {
            return 
                Rlp.LengthOf((int)request.SubType) +
                Rlp.LengthOf(request.SectionIndex) +
                Rlp.LengthOf(request.Key) +
                Rlp.LengthOf(request.FromLevel) +
                Rlp.LengthOf(request.AuxiliaryData);
        }

        public GetHelperTrieProofsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }

        public static GetHelperTrieProofsMessage Deserialize(RlpStream rlpStream)
        {
            GetHelperTrieProofsMessage message = new GetHelperTrieProofsMessage();
            rlpStream.ReadSequenceLength();
            message.RequestId = rlpStream.DecodeLong();
            message.Requests = rlpStream.DecodeArray(stream => { 
                HelperTrieRequest request = new HelperTrieRequest();
                stream.ReadSequenceLength();
                request.SubType = (HelperTrieType)stream.DecodeInt();
                request.SectionIndex = stream.DecodeLong();
                request.Key = stream.DecodeByteArray();
                request.FromLevel = stream.DecodeLong();
                request.AuxiliaryData = stream.DecodeInt();
                return request;
            });
            return message;
        }
    }
}
