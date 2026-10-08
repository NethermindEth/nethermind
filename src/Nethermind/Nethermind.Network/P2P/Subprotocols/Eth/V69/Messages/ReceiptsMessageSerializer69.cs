// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages
{
    public class ReceiptsMessageSerializer69(ISpecProvider specProvider) :
        ReceiptsMessageSerializer(new ReceiptsMessageInnerSerializer69(specProvider)),
        IZeroMessageSerializer<ReceiptsMessage69>
    {
        int IZeroMessageSerializer<ReceiptsMessage69>.GetLength(ReceiptsMessage69 message, out int contentLength) =>
            base.GetLength(message, out contentLength);

        void IZeroMessageSerializer<ReceiptsMessage69>.Serialize(Span<byte> buffer, ReceiptsMessage69 message) =>
            base.Serialize(buffer, message);

        ReceiptsMessage69 IZeroMessageSerializer<ReceiptsMessage69>.Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            ReceiptsMessage message = base.Deserialize(data, out consumed);
            return new(message.RequestId, message.EthMessage);
        }
    }
}
