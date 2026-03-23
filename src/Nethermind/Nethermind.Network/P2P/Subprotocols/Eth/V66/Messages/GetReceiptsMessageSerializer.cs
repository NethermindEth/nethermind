// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetReceiptsMessageSerializer : Eth66MessageSerializer<GetReceiptsMessage>
    {
        private readonly V63.Messages.GetReceiptsMessageSerializer _innerSerializer = new();

        protected override void SerializeInternal(IByteBuffer byteBuffer, GetReceiptsMessage message) =>
            _innerSerializer.Serialize(byteBuffer, message);

        protected override GetReceiptsMessage DeserializeInternal(IByteBuffer byteBuffer, long requestId) =>
            new(requestId, _innerSerializer.Deserialize(byteBuffer));

        protected override int GetLengthInternal(GetReceiptsMessage message) =>
            _innerSerializer.GetLength(message, out _);
    }
}
