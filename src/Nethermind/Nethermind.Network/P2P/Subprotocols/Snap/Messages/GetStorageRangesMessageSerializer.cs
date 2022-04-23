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

using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetStorageRangesMessageSerializer : SnapSerializerBase<GetStorageRangeMessage>
    {

        public override void Serialize(IByteBuffer byteBuffer, GetStorageRangeMessage message)
        {
            NettyRlpStream rlpStream = GetRlpStreamAndStartSequence(byteBuffer, message);

            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(message.StoragetRange.RootHash);
            rlpStream.Encode(message.StoragetRange.Accounts.Select(a => a.Path).ToArray()); // TODO: optimize this
            rlpStream.Encode(message.StoragetRange.StartingHash);
            rlpStream.Encode(message.StoragetRange.LimitHash);
            rlpStream.Encode(message.ResponseBytes);
        }
        
        protected override GetStorageRangeMessage Deserialize(RlpStream rlpStream)
        {
            GetStorageRangeMessage message = new ();
            rlpStream.ReadSequenceLength();

            message.RequestId = rlpStream.DecodeLong();

            message.StoragetRange = new();
            message.StoragetRange.RootHash = rlpStream.DecodeKeccak();
            message.StoragetRange.Accounts = rlpStream.DecodeArray(DecodePathWithRlpData);
            message.StoragetRange.StartingHash = rlpStream.DecodeKeccak();
            message.StoragetRange.LimitHash = rlpStream.DecodeKeccak();
            message.ResponseBytes = rlpStream.DecodeLong();

            return message;
        }

        private PathWithAccount DecodePathWithRlpData(RlpStream stream)
        {
            return new() { Path = stream.DecodeKeccak() };
        }

        public override int GetLength(GetStorageRangeMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.StoragetRange.RootHash);
            contentLength += Rlp.LengthOf(message.StoragetRange.Accounts.Select(a => a.Path).ToArray(), true); // TODO: optimize this
            contentLength += Rlp.LengthOf(message.StoragetRange.StartingHash);
            contentLength += Rlp.LengthOf(message.StoragetRange.LimitHash);
            contentLength += Rlp.LengthOf(message.ResponseBytes);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
