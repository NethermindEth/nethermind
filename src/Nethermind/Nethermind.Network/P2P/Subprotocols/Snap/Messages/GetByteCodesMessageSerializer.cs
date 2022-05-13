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
    public class GetByteCodesMessageSerializer : SnapSerializerBase<GetByteCodesMessage>
    {
        public override void Serialize(IByteBuffer byteBuffer, GetByteCodesMessage message)
        {
            NettyRlpStream rlpStream = GetRlpStreamAndStartSequence(byteBuffer, message);
            
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(message.Hashes);
            rlpStream.Encode(message.Bytes);
        }

        protected override GetByteCodesMessage Deserialize(RlpStream rlpStream)
        {
            GetByteCodesMessage message = new ();
            rlpStream.ReadSequenceLength();

            message.RequestId = rlpStream.DecodeLong();
            message.Hashes = rlpStream.DecodeArray(_ => rlpStream.DecodeKeccak());
            message.Bytes = rlpStream.DecodeLong();

            return message;
        }

        public override int GetLength(GetByteCodesMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.Hashes, true);
            contentLength += Rlp.LengthOf(message.Bytes);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
