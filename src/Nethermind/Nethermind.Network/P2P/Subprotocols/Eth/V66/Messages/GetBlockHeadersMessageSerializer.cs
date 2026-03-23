// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetBlockHeadersMessageSerializer : Eth66MessageSerializer<GetBlockHeadersMessage>
    {
        private readonly V62.Messages.GetBlockHeadersMessageSerializer _innerSerializer = new();

        protected override void SerializeInternal(IByteBuffer byteBuffer, GetBlockHeadersMessage message) =>
            _innerSerializer.Serialize(byteBuffer, message);

        protected override GetBlockHeadersMessage DeserializeInternal(IByteBuffer byteBuffer, long requestId) =>
            new(requestId, _innerSerializer.Deserialize(byteBuffer));

        protected override int GetLengthInternal(GetBlockHeadersMessage message) =>
            _innerSerializer.GetLength(message, out _);
    }
}
