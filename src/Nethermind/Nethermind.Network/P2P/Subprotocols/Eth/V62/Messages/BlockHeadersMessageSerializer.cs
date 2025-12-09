// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Collections.Pooled;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockHeadersMessageSerializer : IZeroInnerMessageSerializer<BlockHeadersMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockHeadersMessage>(NethermindSyncLimits.MaxHeaderFetch, nameof(BlockHeadersMessage.BlockHeaders));
        private readonly IHeaderDecoder _headerDecoder;

        public BlockHeadersMessageSerializer(IHeaderDecoder headerDecoder = null)
        {
            _headerDecoder = headerDecoder ?? new HeaderDecoder();
        }

        public void Serialize(IByteBuffer byteBuffer, BlockHeadersMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);

            rlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.BlockHeaders.Count; i++)
            {
                rlpStream.Encode(message.BlockHeaders[i]);
            }
        }

        public BlockHeadersMessage Deserialize(IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }

        public int GetLength(BlockHeadersMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.BlockHeaders.Count; i++)
            {
                contentLength += _headerDecoder.GetLength(message.BlockHeaders[i], RlpBehaviors.None);
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        public BlockHeadersMessage Deserialize(RlpStream rlpStream)
        {
            BlockHeadersMessage message = new();
            message.BlockHeaders = _headerDecoder.DecodeArrayPool(rlpStream);
            return message;
        }
    }
}
