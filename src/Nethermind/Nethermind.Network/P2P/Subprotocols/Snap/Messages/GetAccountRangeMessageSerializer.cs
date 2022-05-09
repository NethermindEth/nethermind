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
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetAccountRangeMessageSerializer : SnapSerializerBase<GetAccountRangeMessage>
    {
        protected override GetAccountRangeMessage Deserialize(RlpStream rlpStream)
        {
            GetAccountRangeMessage message = new ();
            rlpStream.ReadSequenceLength();

            message.RequestId = rlpStream.DecodeLong();
            message.AccountRange = new(rlpStream.DecodeKeccak(), rlpStream.DecodeKeccak(), rlpStream.DecodeKeccak());
            message.ResponseBytes = rlpStream.DecodeLong();

            return message;
        }

        public override void Serialize(IByteBuffer byteBuffer, GetAccountRangeMessage message)
        {
            NettyRlpStream rlpStream = GetRlpStreamAndStartSequence(byteBuffer, message);
            
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(message.AccountRange.RootHash);
            rlpStream.Encode(message.AccountRange.StartingHash);

            rlpStream.Encode(message.AccountRange.LimitHash ?? Keccak.MaxValue);
            rlpStream.Encode(message.ResponseBytes == 0 ? 1000_000 : message.ResponseBytes);
        }

        public override int GetLength(GetAccountRangeMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.AccountRange.RootHash);
            contentLength += Rlp.LengthOf(message.AccountRange.StartingHash);
            contentLength += Rlp.LengthOf(message.AccountRange.LimitHash);
            contentLength += Rlp.LengthOf(message.ResponseBytes);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
