// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockHeadersMessageSerializer(IHeaderDecoder? headerDecoder = null) : IZeroInnerMessageSerializer<BlockHeadersMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockHeadersMessage>(NethermindSyncLimits.MaxHeaderFetch, nameof(BlockHeadersMessage.BlockHeaders));
        private readonly IHeaderDecoder _headerDecoder = headerDecoder ?? new HeaderDecoder();

        public void Serialize(IByteBuffer byteBuffer, BlockHeadersMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            ByteBufferRlpWriter writer = new(byteBuffer);

            writer.StartSequence(contentLength);
            System.ReadOnlySpan<BlockHeader> blockHeaders = message.BlockHeaders is null ? [] : message.BlockHeaders.AsSpan();
            for (int i = 0; i < blockHeaders.Length; i++)
            {
                _headerDecoder.Encode(ref writer, blockHeaders[i]);
            }
        }

        public BlockHeadersMessage Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(Deserialize) ?? throw new RlpException("Block headers message decoding returned null.");

        public int GetLength(BlockHeadersMessage message, out int contentLength)
        {
            contentLength = 0;
            System.ReadOnlySpan<BlockHeader> blockHeaders = message.BlockHeaders is null ? [] : message.BlockHeaders.AsSpan();
            for (int i = 0; i < blockHeaders.Length; i++)
            {
                contentLength += _headerDecoder.GetLength(blockHeaders[i], RlpBehaviors.None);
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        public BlockHeadersMessage Deserialize(ref RlpReader ctx)
        {
            BlockHeadersMessage message = new();
            message.BlockHeaders = ctx.DecodeArrayPoolList((ref RlpReader c) => _headerDecoder.DecodeGuardNotNull(ref c), limit: RlpLimit);
            return message;
        }
    }
}
