// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Specs;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages
{
    public class ReceiptsMessageSerializer:
        Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessageSerializer,
        IZeroInnerMessageSerializer<ReceiptsMessage>
    {
        public ReceiptsMessageSerializer(ISpecProvider specProvider): base(new ReceiptsMessageInnerSerializer(specProvider)) { }

        int IZeroInnerMessageSerializer<ReceiptsMessage>.GetLength(ReceiptsMessage message, out int contentLength) =>
            base.GetLength(message, out contentLength);

        void IZeroMessageSerializer<ReceiptsMessage>.Serialize(IByteBuffer byteBuffer, ReceiptsMessage message) =>
            base.Serialize(byteBuffer, message);

        ReceiptsMessage IZeroMessageSerializer<ReceiptsMessage>.Deserialize(IByteBuffer byteBuffer)
        {
            V66.Messages.ReceiptsMessage message = base.Deserialize(byteBuffer);
            return new ReceiptsMessage(message.RequestId, message.EthMessage);
        }
    }
}
