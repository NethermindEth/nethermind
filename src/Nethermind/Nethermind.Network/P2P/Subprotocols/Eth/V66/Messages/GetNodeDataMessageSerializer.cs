// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetNodeDataMessageSerializer : Eth66MessageSerializer<GetNodeDataMessage>
    {
        private readonly V63.Messages.GetNodeDataMessageSerializer _innerSerializer = new();

        protected override void SerializeInternal(IByteBuffer byteBuffer, GetNodeDataMessage message) =>
            _innerSerializer.Serialize(byteBuffer, message);

        protected override GetNodeDataMessage DeserializeInternal(IByteBuffer byteBuffer, long requestId) =>
            new(requestId, _innerSerializer.Deserialize(byteBuffer));

        protected override int GetLengthInternal(GetNodeDataMessage message) =>
            _innerSerializer.GetLength(message, out _);
    }
}
