// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class BlockHeadersMessageSerializer : Eth66MessageSerializer<BlockHeadersMessage>
    {
        private readonly V62.Messages.BlockHeadersMessageSerializer _innerSerializer = new();

        protected override void SerializeInternal(IByteBuffer byteBuffer, BlockHeadersMessage message) =>
            _innerSerializer.Serialize(byteBuffer, message);

        protected override BlockHeadersMessage DeserializeInternal(IByteBuffer byteBuffer, long requestId) =>
            new(requestId, _innerSerializer.Deserialize(byteBuffer));

        protected override int GetLengthInternal(BlockHeadersMessage message) =>
            _innerSerializer.GetLength(message, out _);
    }
}
