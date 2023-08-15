// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockBodiesMessageSerializer : IZeroInnerMessageSerializer<BlockBodiesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, BlockBodiesMessage message)
        {
            int totalLength = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(totalLength, true);
            NettyRlpStream stream = new(byteBuffer);
            message.Bodies.SerializeBodies(stream);
        }

        public BlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        public int GetLength(BlockBodiesMessage message, out int contentLength)
        {
            (int rlpLength, contentLength) = message.Bodies.RlpLength();
            return rlpLength;
        }

        public static BlockBodiesMessage Deserialize(RlpStream rlpStream)
        {
            (Memory<byte> memory, IMemoryOwner<byte> memOwner) = rlpStream.ReadItem();
            // TODO: Can read directly from netty buffer instead of creating new memory from pool
            UnmanagedBlockBodies unmanagedBlockBodies = new(memory, memOwner);
            return new BlockBodiesMessage(unmanagedBlockBodies);
        }
    }
}
