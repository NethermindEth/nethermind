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

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public class NewBlockHashesMessageSerializer : IZeroMessageSerializer<NewBlockHashesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, NewBlockHashesMessage message)
        {
            NettyRlpStream nettyRlpStream = new(byteBuffer);

            int contentLength = 0;
            for (int i = 0; i < message.BlockHashes.Length; i++)
            {
                int miniContentLength = Rlp.LengthOf(message.BlockHashes[i].Item1);
                miniContentLength += Rlp.LengthOf(message.BlockHashes[i].Item2);
                contentLength += Rlp.GetSequenceRlpLength(miniContentLength);
            }

            int totalLength = Rlp.LengthOfSequence(contentLength);
            byteBuffer.EnsureWritable(totalLength, true);
            
            nettyRlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.BlockHashes.Length; i++)
            {
                int miniContentLength = Rlp.LengthOf(message.BlockHashes[i].Item1);
                miniContentLength += Rlp.LengthOf(message.BlockHashes[i].Item2);
                nettyRlpStream.StartSequence(miniContentLength);
                nettyRlpStream.Encode(message.BlockHashes[i].Item1);
                nettyRlpStream.Encode(message.BlockHashes[i].Item2);
            }
        }

        public NewBlockHashesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }
        
        private static NewBlockHashesMessage Deserialize(RlpStream rlpStream)
        {
            (Keccak, long)[] blockHashes = rlpStream.DecodeArray(ctx =>
            {
                ctx.ReadSequenceLength();
                return (ctx.DecodeKeccak(), (long) ctx.DecodeUInt256());
            }, false);

            return new NewBlockHashesMessage(blockHashes);
        }
    }
}
