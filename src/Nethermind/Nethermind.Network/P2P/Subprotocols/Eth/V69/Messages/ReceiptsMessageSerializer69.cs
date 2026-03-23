// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using DotNetty.Buffers;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages
{
    public class ReceiptsMessageSerializer69 : Eth66MessageSerializer<ReceiptsMessage69>
    {
        private readonly IZeroInnerMessageSerializer<ReceiptsInnerMessage69> _innerSerializer;

        public ReceiptsMessageSerializer69(ISpecProvider specProvider)
        {
            _innerSerializer = new ReceiptsMessageInnerSerializer69(specProvider);
        }

        protected override void SerializeInternal(IByteBuffer byteBuffer, ReceiptsMessage69 message) =>
            _innerSerializer.Serialize(byteBuffer, new ReceiptsInnerMessage69(message.TxReceipts));

        protected override ReceiptsMessage69 DeserializeInternal(IByteBuffer byteBuffer, long requestId) =>
            new(requestId, _innerSerializer.Deserialize(byteBuffer));

        protected override int GetLengthInternal(ReceiptsMessage69 message) =>
            _innerSerializer.GetLength(new ReceiptsInnerMessage69(message.TxReceipts), out _);
    }
}
