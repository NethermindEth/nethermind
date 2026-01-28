// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockBodiesMessageSerializer : IZeroInnerMessageSerializer<BlockBodiesMessage>
    {
        /// <summary>
        /// Maximum total RLP elements allowed in a block bodies message.
        /// Prevents nested amplification DOS (e.g., 256 bodies × 10000 transactions × access lists).
        /// Set to 2M to allow legitimate large blocks (each tx has ~10 RLP fields) while preventing memory DOS.
        /// </summary>
        private const int MaxTotalElements = 2_000_000;

        private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockBodiesMessage>(NethermindSyncLimits.MaxBodyFetch, nameof(BlockBodiesMessage.Bodies));
        private readonly BlockBodyDecoder _blockBodyDecoder = BlockBodyDecoder.Instance;

        public void Serialize(IByteBuffer byteBuffer, BlockBodiesMessage message)
        {
            int totalLength = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(totalLength);
            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(contentLength);
            foreach (BlockBody? body in message.Bodies.Bodies)
            {
                if (body is null)
                {
                    stream.Encode(Rlp.OfEmptySequence);
                }
                else
                {
                    _blockBodyDecoder.Encode(stream, body);
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
                    null => Rlp.OfEmptySequence.Length,
                    _ => Rlp.LengthOfSequence(_blockBodyDecoder.GetBodyLength(body))
                };
            }

            contentLength = length;
            return Rlp.LengthOfSequence(length);
        }

        public BlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            // Pass 1: Validate nested structure to prevent memory DOS
            int originalReaderIndex = byteBuffer.ReaderIndex;
            NettyRlpStream countingStream = new(byteBuffer);
            RlpElementCounter.CountElementsInSequence(countingStream, MaxTotalElements);

            // Pass 2: Actual decode (limits validated, safe to allocate)
            byteBuffer.SetReaderIndex(originalReaderIndex);
            NettyBufferMemoryOwner memoryOwner = new(byteBuffer);

            Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, true);
            int startingPosition = ctx.Position;
            BlockBody[]? bodies = ctx.DecodeArray(_blockBodyDecoder, false, limit: RlpLimit);
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startingPosition));

            return new() { Bodies = new(bodies, memoryOwner) };
        }
    }
}
