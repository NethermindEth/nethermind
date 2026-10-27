// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockHeadersMessageSerializer(IHeaderDecoder headerDecoder = null) : IZeroInnerMessageSerializer<BlockHeadersMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockHeadersMessage>(NethermindSyncLimits.MaxHeaderFetch, nameof(BlockHeadersMessage.BlockHeaders));
        private readonly IHeaderDecoder _headerDecoder = headerDecoder ?? new HeaderDecoder();

        public void Serialize(IByteBuffer byteBuffer, BlockHeadersMessage message) => this.SerializeWithLengths(byteBuffer, message);

        public void Serialize(IByteBuffer byteBuffer, BlockHeadersMessage message, ref LengthStack lengths)
        {
            ByteBufferRlpWriter writer = new(byteBuffer);
            _headerDecoder.EncodeSequence(ref writer, message.BlockHeaders.AsSpan(), ref lengths);
        }

        public BlockHeadersMessage Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(Deserialize);

        public int PopulateLength(BlockHeadersMessage message, ref LengthStack lengths) =>
            _headerDecoder.PopulateSequenceLength(message.BlockHeaders.AsSpan(), ref lengths);

        public BlockHeadersMessage Deserialize(ref RlpReader ctx)
        {
            BlockHeadersMessage message = new();
            message.BlockHeaders = Rlp.DecodeArrayPool(ref ctx, _headerDecoder, limit: RlpLimit);
            return message;
        }
    }
}
