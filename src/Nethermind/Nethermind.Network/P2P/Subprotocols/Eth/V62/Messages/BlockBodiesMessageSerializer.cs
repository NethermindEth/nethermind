// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockBodiesMessageSerializer(BlockBodyDecoder blockBodyDecoder = null) : IZeroInnerMessageSerializer<BlockBodiesMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockBodiesMessage>(NethermindSyncLimits.MaxBodyFetch, nameof(BlockBodiesMessage.Bodies));
        private readonly BlockBodyDecoder _blockBodyDecoder = blockBodyDecoder ?? BlockBodyDecoder.Instance;

        public void Serialize(IByteBuffer byteBuffer, BlockBodiesMessage message)
        {
            int totalLength = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(totalLength);
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(contentLength);
            foreach (BlockBody? body in message.Bodies.Bodies)
            {
                if (body is null)
                {
                    writer.Encode(Rlp.OfEmptyList);
                }
                else
                {
                    _blockBodyDecoder.Encode(ref writer, body);
                }
            }
        }

        public int GetLength(BlockBodiesMessage message, out int contentLength)
        {
            int length = 0;
            foreach (BlockBody? body in message.Bodies.Bodies)
            {
                length += body switch
                {
                    null => Rlp.OfEmptyList.Length,
                    _ => Rlp.LengthOfSequence(_blockBodyDecoder.GetBodyLength(body))
                };
            }

            contentLength = length;
            return Rlp.LengthOfSequence(length);
        }

        public BlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner? memoryOwner = new(byteBuffer);
            OwnedBlockBodies? ownedBodies = null;

            RlpReader ctx = new(memoryOwner.Memory.Span);
            int startingPosition = ctx.Position;
            try
            {
                ctx.ReadSequenceLength();
                int count = ctx.PeekNumberOfItemsRemaining(ctx.Length, RlpLimit.Limit + 1);
                ctx.GuardLimit(count, RlpLimit);
                BlockBody?[] bodies = new BlockBody?[count];
                ownedBodies = new(bodies, memoryOwner, ownsPooledTransactions: true);
                memoryOwner = null;
                for (int i = 0; i < count; i++) bodies[i] = _blockBodyDecoder.Decode(ref ctx);
                byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startingPosition));

                return new() { Bodies = ownedBodies };
            }
            catch
            {
                ownedBodies?.Dispose();
                memoryOwner?.Dispose();
                throw;
            }
        }
    }
}
