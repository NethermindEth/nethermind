// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Specs;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages
{
    public class ReceiptsMessageSerializer69(ISpecProvider specProvider) :
        ReceiptsMessageSerializer(new ReceiptsMessageInnerSerializer69(specProvider)),
        IZeroInnerMessageSerializer<ReceiptsMessage69>
    {
        int IZeroInnerMessageSerializer<ReceiptsMessage69>.PopulateLength(ReceiptsMessage69 message, ref LengthStack lengths) =>
            base.PopulateLength(message, ref lengths);

        void IZeroInnerMessageSerializer<ReceiptsMessage69>.Serialize(IByteBuffer byteBuffer, ReceiptsMessage69 message, ref LengthStack lengths) =>
            base.Serialize(byteBuffer, message, ref lengths);

        void IZeroMessageSerializer<ReceiptsMessage69>.Serialize(IByteBuffer byteBuffer, ReceiptsMessage69 message) =>
            base.Serialize(byteBuffer, message);

        ReceiptsMessage69 IZeroMessageSerializer<ReceiptsMessage69>.Deserialize(IByteBuffer byteBuffer)
        {
            ReceiptsMessage message = base.Deserialize(byteBuffer);
            return new(message.RequestId, message.EthMessage);
        }
    }
}
