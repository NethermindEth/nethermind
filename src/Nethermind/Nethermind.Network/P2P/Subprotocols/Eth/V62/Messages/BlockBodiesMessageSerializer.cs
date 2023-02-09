// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockBodiesMessageSerializer : IZeroInnerMessageSerializer<BlockBodiesMessage>
    {
        public byte[] Serialize(BlockBodiesMessage message) =>
            Rlp.Encode(message.Bodies.Select(b => b is null
                ? Rlp.OfEmptySequence
                : b.Withdrawals != null
                    ? Rlp.Encode(Rlp.Encode(b.Transactions), Rlp.Encode(b.Uncles), Rlp.Encode(b.Withdrawals))
                    : Rlp.Encode(Rlp.Encode(b.Transactions), Rlp.Encode(b.Uncles))
                ).ToArray()).Bytes;

        public void Serialize(IByteBuffer byteBuffer, BlockBodiesMessage message)
        {
            byte[] oldWay = Serialize(message);
            byteBuffer.EnsureWritable(oldWay.Length, true);
            byteBuffer.WriteBytes(oldWay);
        }

        public BlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        public int GetLength(BlockBodiesMessage message, out int contentLength)
        {
            byte[] oldWay = Serialize(message);
            contentLength = oldWay.Length;
            return contentLength;
        }

        public static BlockBodiesMessage Deserialize(RlpStream rlpStream)
        {
            BlockBodiesMessage message = new();
            message.Bodies = rlpStream.DecodeArray(ctx =>
            {
                int sequenceLength = rlpStream.ReadSequenceLength();
                int startingPosition = rlpStream.Position;
                if (sequenceLength == 0)
                {
                    return null;
                }

                // quite significant allocations (>0.5%) here based on a sample 3M blocks sync
                // (just on these delegates)
                Transaction[] transactions = rlpStream.DecodeArray(_ => Rlp.Decode<Transaction>(ctx));
                BlockHeader[] uncles = rlpStream.DecodeArray(_ => Rlp.Decode<BlockHeader>(ctx));
                Withdrawal[]? withdrawals = null;
                if (rlpStream.PeekNumberOfItemsRemaining(startingPosition + sequenceLength, 1) > 0)
                {
                    withdrawals = rlpStream.DecodeArray(_ => Rlp.Decode<Withdrawal>(ctx));
                }

                return new BlockBody(transactions, uncles, withdrawals);
            }, false);

            return message;
        }
    }
}
