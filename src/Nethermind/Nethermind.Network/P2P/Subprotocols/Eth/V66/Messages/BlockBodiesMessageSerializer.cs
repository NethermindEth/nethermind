// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class BlockBodiesMessageSerializer : Eth66MessageSerializer<BlockBodiesMessage>
    {
        private readonly V62.Messages.BlockBodiesMessageSerializer _innerSerializer = new();

        protected override void SerializeInternal(IByteBuffer byteBuffer, BlockBodiesMessage message) =>
            _innerSerializer.Serialize(byteBuffer, message);

        protected override BlockBodiesMessage DeserializeInternal(IByteBuffer byteBuffer, long requestId) =>
            new(requestId, _innerSerializer.Deserialize(byteBuffer));

        protected override int GetLengthInternal(BlockBodiesMessage message) =>
            _innerSerializer.GetLength(message, out _);
    }
}
