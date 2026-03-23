// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class PooledTransactionsMessageSerializer : Eth66MessageSerializer<PooledTransactionsMessage>
    {
        private readonly V65.Messages.PooledTransactionsMessageSerializer _innerSerializer = new();

        protected override void SerializeInternal(IByteBuffer byteBuffer, PooledTransactionsMessage message) =>
            _innerSerializer.Serialize(byteBuffer, message);

        protected override PooledTransactionsMessage DeserializeInternal(IByteBuffer byteBuffer, long requestId) =>
            new(requestId, _innerSerializer.Deserialize(byteBuffer));

        protected override int GetLengthInternal(PooledTransactionsMessage message) =>
            _innerSerializer.GetLength(message, out _);
    }
}
