// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    /// <remarks>
    /// The bodies are not decoded here: each stays encoded until a consumer decodes it, so a consumer that only
    /// stores bodies does not decode their transactions.
    /// </remarks>
    public class BlockBodiesMessageSerializer(BlockBodyDecoder blockBodyDecoder = null) : IMessageSerializer<BlockBodiesMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockBodiesMessage>(NethermindSyncLimits.MaxBodyFetch, nameof(BlockBodiesMessage.Bodies));
        private readonly BlockBodyDecoder _blockBodyDecoder = blockBodyDecoder ?? BlockBodyDecoder.Instance;

        public int Serialize(Span<byte> destination, BlockBodiesMessage message)
        {
            RlpWriter writer = RlpWriter.ContentWriter(destination);
            RlpSequence sequence = writer.StartSequence(MessageSequence.GetContentLength(destination.Length));
            OwnedBlockBodies bodies = message.Bodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                writer.WriteEncoded(bodies.GetEncoded(i).Span);
            }

            writer.EndSequence(sequence);
            return writer.Length;
        }

        public int GetLength(BlockBodiesMessage message) => Rlp.LengthOfSequence(GetContentLength(message));

        private static int GetContentLength(BlockBodiesMessage message)
        {
            OwnedBlockBodies bodies = message.Bodies;
            int length = 0;
            for (int i = 0; i < bodies.Count; i++)
            {
                length += bodies.GetEncoded(i).Length;
            }

            return length;
        }

        public BlockBodiesMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            RlpReader items = ctx.EnterSequence(out int itemLength);
            int itemsStart = itemLength - items.Remaining;
            Range[] encodedBodies = new Range[items.CountItems(RlpLimit)];
            for (int i = 0; i < encodedBodies.Length; i++)
            {
                if (!items.IsNextSequence) throw new RlpReaderException($"Block body {i} is not a sequence");
                int start = itemsStart + items.BytesRead;
                encodedBodies[i] = new Range(start, start + items.ReadEncodedItem().Length);
            }

            ctx.ExitSequence(items, itemLength);
            ctx.CheckEnd();

            IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(data.Length);
            data.CopyTo(memory.Memory.Span);
            return new() { Bodies = new OwnedBlockBodies(memory, encodedBodies, _blockBodyDecoder) };
        }
    }
}
