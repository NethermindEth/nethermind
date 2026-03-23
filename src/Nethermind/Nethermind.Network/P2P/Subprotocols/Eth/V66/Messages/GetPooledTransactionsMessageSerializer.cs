// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetPooledTransactionsMessageSerializer : Eth66MessageSerializer<GetPooledTransactionsMessage>
    {
        private readonly V65.Messages.GetPooledTransactionsMessageSerializer _innerSerializer = new();

        protected override void SerializeInternal(IByteBuffer byteBuffer, GetPooledTransactionsMessage message) =>
            _innerSerializer.Serialize(byteBuffer, message);

        protected override GetPooledTransactionsMessage DeserializeInternal(IByteBuffer byteBuffer, long requestId) =>
            new(requestId, _innerSerializer.Deserialize(byteBuffer));

        protected override int GetLengthInternal(GetPooledTransactionsMessage message) =>
            _innerSerializer.GetLength(message, out _);
    }
}
