// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages
{
    public class ReceiptsMessageSerializer69(ISpecProvider specProvider) :
        ReceiptsMessageSerializer(new ReceiptsMessageInnerSerializer69(specProvider)),
        IMessageSerializer<ReceiptsMessage69>
    {
        int IMessageSerializer<ReceiptsMessage69>.GetLength(ReceiptsMessage69 message) =>
            base.GetLength(message);

        int IMessageSerializer<ReceiptsMessage69>.Serialize(Span<byte> destination, ReceiptsMessage69 message) =>
            base.Serialize(destination, message);

        ReceiptsMessage69 IMessageSerializer<ReceiptsMessage69>.Deserialize(ReadOnlySpan<byte> data)
        {
            ReceiptsMessage message = base.Deserialize(data);
            return new(message.RequestId, message.EthMessage);
        }
    }
}
