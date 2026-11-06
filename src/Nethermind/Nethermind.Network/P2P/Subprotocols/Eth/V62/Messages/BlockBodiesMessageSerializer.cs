// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockBodiesMessageSerializer(BlockBodyDecoder blockBodyDecoder = null) : IMessageSerializer<BlockBodiesMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockBodiesMessage>(NethermindSyncLimits.MaxBodyFetch, nameof(BlockBodiesMessage.Bodies));
        private readonly BlockBodyDecoder _blockBodyDecoder = blockBodyDecoder ?? BlockBodyDecoder.Instance;

        public int Serialize(Span<byte> destination, BlockBodiesMessage message)
        {
            RlpWriter writer = RlpWriter.ContentWriter(destination);
            RlpSequence sequence = writer.StartSequence(GetContentLength(message));
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

            writer.EndSequence(sequence);
            return writer.Length;
        }

        public int GetLength(BlockBodiesMessage message) => Rlp.LengthOfSequence(GetContentLength(message));

        private int GetContentLength(BlockBodiesMessage message)
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

            return length;
        }

        public BlockBodiesMessage Deserialize(ReadOnlySpan<byte> data)
        {
            OwnedBlockBodies? ownedBodies = null;

            RlpReader ctx = new(data);
            try
            {
                RlpReader items = ctx.EnterSequence(out int itemLength);
                int count = items.CountItems(RlpLimit);
                BlockBody?[] bodies = new BlockBody?[count];
                ownedBodies = new(bodies, ownsPooledTransactions: true);
                for (int i = 0; i < count; i++) bodies[i] = _blockBodyDecoder.Decode(ref items);
                ctx.ExitSequence(items, itemLength);
                ctx.CheckEnd();

                return new() { Bodies = ownedBodies };
            }
            catch
            {
                ownedBodies?.Dispose();
                throw;
            }
        }
    }
}
