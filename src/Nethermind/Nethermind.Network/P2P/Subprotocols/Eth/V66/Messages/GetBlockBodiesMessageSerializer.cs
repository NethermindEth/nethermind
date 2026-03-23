// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetBlockBodiesMessageSerializer : Eth66MessageSerializer<GetBlockBodiesMessage>
    {
        private readonly V62.Messages.GetBlockBodiesMessageSerializer _innerSerializer = new();

        protected override void SerializeInternal(IByteBuffer byteBuffer, GetBlockBodiesMessage message) =>
            _innerSerializer.Serialize(byteBuffer, message);

        protected override GetBlockBodiesMessage DeserializeInternal(IByteBuffer byteBuffer, long requestId) =>
            new(requestId, _innerSerializer.Deserialize(byteBuffer));

        protected override int GetLengthInternal(GetBlockBodiesMessage message) =>
            _innerSerializer.GetLength(message, out _);
    }
}
