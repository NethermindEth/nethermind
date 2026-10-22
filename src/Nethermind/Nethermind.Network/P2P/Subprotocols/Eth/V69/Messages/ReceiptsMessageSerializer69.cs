// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Specs;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages
{
    public class ReceiptsMessageSerializer69(ISpecProvider specProvider) :
        ReceiptsMessageSerializer(new ReceiptsMessageInnerSerializer69(specProvider)),
        IZeroInnerMessageSerializer<ReceiptsMessage69>
    {
        int IZeroInnerMessageSerializer<ReceiptsMessage69>.GetLength(ReceiptsMessage69 message, out int contentLength) =>
            base.GetLength(message, out contentLength);

        void IZeroMessageSerializer<ReceiptsMessage69>.Serialize(IByteBuffer byteBuffer, ReceiptsMessage69 message) =>
            base.Serialize(byteBuffer, message);

        ReceiptsMessage69 IZeroMessageSerializer<ReceiptsMessage69>.Deserialize(IByteBuffer byteBuffer)
        {
            ReceiptsMessage message = base.Deserialize(byteBuffer);
            return new(message.RequestId, message.EthMessage);
        }
    }
}
