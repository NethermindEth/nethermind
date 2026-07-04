// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockBodiesMessageSerializer : IZeroInnerMessageSerializer<BlockBodiesMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockBodiesMessage>(NethermindSyncLimits.MaxBodyFetch, nameof(BlockBodiesMessage.Bodies));

        public void Serialize(IByteBuffer byteBuffer, BlockBodiesMessage message)
        {
            int totalLength = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(totalLength);
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(contentLength);
            RlpBlockBodies bodies = message.Bodies!;
            for (int i = 0; i < bodies.Count; i++)
            {
                RlpBlockBody? body = bodies.GetRawBody(i);
                if (body is null)
                {
                    writer.Encode(Rlp.OfEmptyList);
                }
                else
                {
                    body.Write(ref writer);
                }
            }
        }

        public int GetLength(BlockBodiesMessage message, out int contentLength)
        {
            int length = 0;
            RlpBlockBodies bodies = message.Bodies!;
            for (int i = 0; i < bodies.Count; i++)
            {
                length += bodies.GetRawBody(i)?.RlpLength ?? Rlp.OfEmptyList.Length;
            }

            contentLength = length;
            return Rlp.LengthOfSequence(length);
        }

        public BlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner memoryOwner = new(byteBuffer);
            // The owner's Memory is anchored at the buffer's current reader index, so it must be snapshotted
            // (and all body regions sliced from it) before SetReaderIndex advances the buffer.
            Memory<byte> memory = memoryOwner.Memory;

            RefCountingMemoryOwner<byte>? sharedOwner = null;
            RlpBlockBody?[]? bodies = null;
            int created = 0;
            try
            {
                RlpReader ctx = new(memory.Span);
                int startingPosition = ctx.Position;
                int innerLength = ctx.ReadSequenceLength();
                int end = ctx.Position + innerLength;
                int count = ctx.PeekNumberOfItemsRemaining(end, RlpLimit.Limit + 1);
                ctx.GuardLimit(count, RlpLimit);

                sharedOwner = new RefCountingMemoryOwner<byte>(memoryOwner);
                bodies = new RlpBlockBody?[count];
                for (int i = 0; i < count; i++)
                {
                    if (memory.Span[ctx.Position] == Rlp.OfEmptyList[0])
                    {
                        // An empty list marks a body absent from the response.
                        ctx.SkipItem();
                        continue;
                    }

                    int itemLength = ctx.PeekNextRlpLength();
                    sharedOwner.AcquireLease();
                    try
                    {
                        bodies[i] = RlpBlockBody.FromBodyItem(sharedOwner, memory.Slice(ctx.Position, itemLength));
                    }
                    catch
                    {
                        sharedOwner.Dispose(); // Release the lease acquired for the failed body
                        throw;
                    }

                    created = i + 1;
                    ctx.Position += itemLength;
                }

                ctx.Check(end);
                byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startingPosition));

                return new() { Bodies = new RlpBlockBodies(bodies, sharedOwner) };
            }
            catch
            {
                for (int i = 0; i < created; i++)
                {
                    bodies![i]?.Dispose();
                }

                if (sharedOwner is not null)
                {
                    sharedOwner.Dispose();
                }
                else
                {
                    memoryOwner.Dispose();
                }

                throw;
            }
        }
    }
}
