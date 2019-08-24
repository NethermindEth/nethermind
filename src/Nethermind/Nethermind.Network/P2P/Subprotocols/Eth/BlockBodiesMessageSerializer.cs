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

using System;
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class BlockBodiesMessageSerializer : IMessageSerializer<BlockBodiesMessage>, IZeroMessageSerializer<BlockBodiesMessage>
    {
        public byte[] Serialize(BlockBodiesMessage message)
        {
            return Rlp.Encode(message.Bodies.Select(b => b == null
                ? Rlp.OfEmptySequence
                : Rlp.Encode(
                    Rlp.Encode(b.Transactions),
                    Rlp.Encode(b.Ommers))).ToArray()).Bytes;
        }

        public BlockBodiesMessage Deserialize(byte[] bytes)
        {
            RlpStream rlpStream = bytes.AsRlpStream();
            return Deserialize(rlpStream);
        }

        private static BlockBodiesMessage Deserialize(RlpStream rlpStream)
        {
            BlockBodiesMessage message = new BlockBodiesMessage();
            message.Bodies = rlpStream.DecodeArray(ctx =>
            {
                int sequenceLength = rlpStream.ReadSequenceLength();
                if (sequenceLength == 0)
                {
                    return null;
                }

                Transaction[] transactions = rlpStream.DecodeArray(txCtx => Rlp.Decode<Transaction>(ctx));
                BlockHeader[] ommers = rlpStream.DecodeArray(txCtx => Rlp.Decode<BlockHeader>(ctx));
                return new BlockBody(transactions, ommers);
            }, false);

            return message;
        }

        public void Serialize(IByteBuffer byteBuffer, BlockBodiesMessage message)
        {
            byte[] oldWay = Serialize(message);
            byteBuffer.EnsureWritable(oldWay.Length, true);
            byteBuffer.WriteBytes(oldWay);
        }

        public BlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }
    }
}