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
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public abstract class HashesMessageSerializer<T> : IZeroMessageSerializer<T> where T : HashesMessage
    {
        protected Keccak[] DeserializeHashes(IByteBuffer byteBuffer)
        {
            NettyRlpStream nettyRlpStream = new(byteBuffer);
            return DeserializeHashes(nettyRlpStream);
        }

        protected static Keccak[] DeserializeHashes(RlpStream rlpStream)
        {
            Keccak[] hashes = rlpStream.DecodeArray(itemContext => itemContext.DecodeKeccak());
            return hashes;
        }

        public void Serialize(IByteBuffer byteBuffer, T message)
        {
            int contentLength = 0;
            for (int i = 0; i < message.Hashes.Count; i++)
            {
                contentLength += Rlp.LengthOf(message.Hashes[i]);
            }

            int totalLength = Rlp.LengthOfSequence(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength, true);
            rlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.Hashes.Count; i++)
            {
                rlpStream.Encode(message.Hashes[i]);
            }
        }

        public abstract T Deserialize(IByteBuffer byteBuffer);
    }
}
