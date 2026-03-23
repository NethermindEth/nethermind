// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class ReceiptsMessageSerializer : Eth66MessageSerializer<ReceiptsMessage>
    {
        private readonly IZeroInnerMessageSerializer<V63.Messages.ReceiptsMessage> _innerSerializer;

        public ReceiptsMessageSerializer(IZeroInnerMessageSerializer<V63.Messages.ReceiptsMessage> innerSerializer)
        {
            _innerSerializer = innerSerializer;
        }

        protected override void SerializeInternal(IByteBuffer byteBuffer, ReceiptsMessage message) =>
            _innerSerializer.Serialize(byteBuffer, message);

        protected override ReceiptsMessage DeserializeInternal(IByteBuffer byteBuffer, long requestId) =>
            new(requestId, _innerSerializer.Deserialize(byteBuffer));

        protected override int GetLengthInternal(ReceiptsMessage message) =>
            _innerSerializer.GetLength(message, out _);
    }
}
