// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class NodeDataMessageSerializer : Eth66MessageSerializer<NodeDataMessage>
    {
        private readonly V63.Messages.NodeDataMessageSerializer _innerSerializer = new();

        protected override void SerializeInternal(IByteBuffer byteBuffer, NodeDataMessage message) =>
            _innerSerializer.Serialize(byteBuffer, message);

        protected override NodeDataMessage DeserializeInternal(IByteBuffer byteBuffer, long requestId) =>
            new(requestId, _innerSerializer.Deserialize(byteBuffer));

        protected override int GetLengthInternal(NodeDataMessage message) =>
            _innerSerializer.GetLength(message, out _);
    }
}
