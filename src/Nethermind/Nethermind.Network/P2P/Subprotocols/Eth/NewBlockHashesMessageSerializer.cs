/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */


using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class NewBlockHashesMessageSerializer : IMessageSerializer<NewBlockHashesMessage>, IZeroMessageSerializer<NewBlockHashesMessage>
    {
        public byte[] Serialize(NewBlockHashesMessage message)
        {
            return Rlp.Encode(
                message.BlockHashes.Select(bh =>
                    Rlp.Encode(
                        Rlp.Encode(bh.Item1),
                        Rlp.Encode(bh.Item2))).ToArray()
            ).Bytes;
        }

        public NewBlockHashesMessage Deserialize(byte[] bytes)
        {
            RlpStream rlpStream = bytes.AsRlpStream();
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

        public void Serialize(IByteBuffer byteBuffer, NewBlockHashesMessage message)
        {
            NettyRlpStream nettyRlpStream = new NettyRlpStream(byteBuffer);

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
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }
    }
}