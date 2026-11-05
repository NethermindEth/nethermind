// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockHeadersMessageSerializer(IHeaderDecoder headerDecoder = null) : IMessageSerializer<BlockHeadersMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockHeadersMessage>(NethermindSyncLimits.MaxHeaderFetch, nameof(BlockHeadersMessage.BlockHeaders));
        private readonly IHeaderDecoder _headerDecoder = headerDecoder ?? new HeaderDecoder();

        public int Serialize(Span<byte> destination, BlockHeadersMessage message)
        {
            RlpWriter writer = new(destination);

            writer.StartSequence(GetContentLength(message));
            ReadOnlySpan<BlockHeader> blockHeaders = message.BlockHeaders.AsSpan();
            for (int i = 0; i < blockHeaders.Length; i++)
            {
                _headerDecoder.Encode(ref writer, blockHeaders[i]);
            }

            return writer.Position;
        }

        public BlockHeadersMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            return ctx.CheckEnd(Deserialize(ref ctx));
        }

        public int GetLength(BlockHeadersMessage message) => Rlp.LengthOfSequence(GetContentLength(message));

        private int GetContentLength(BlockHeadersMessage message)
        {
            int contentLength = 0;
            ReadOnlySpan<BlockHeader> blockHeaders = message.BlockHeaders.AsSpan();
            for (int i = 0; i < blockHeaders.Length; i++)
            {
                contentLength += _headerDecoder.GetLength(blockHeaders[i], RlpBehaviors.None);
            }

            return contentLength;
        }

        private BlockHeadersMessage Deserialize(ref RlpReader ctx)
        {
            BlockHeadersMessage message = new();
            message.BlockHeaders = Rlp.DecodeArrayPool(ref ctx, _headerDecoder, limit: RlpLimit);
            return message;
        }
    }
}
