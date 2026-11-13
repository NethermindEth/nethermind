// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

/// <remarks>
/// "Inner" serializer here inherits and overrides parts of eth/63 implementation,
/// while <see cref="ReceiptsMessageSerializer69"/> "wraps" it after, similar to eth/66 version.
/// </remarks>
public class ReceiptsMessageInnerSerializer69(ISpecProvider specProvider) :
    ReceiptsMessageSerializer(specProvider, new ReceiptMessageDecoder69()),
    IMessageSerializer<ReceiptsInnerMessage69>
{
    int IMessageSerializer<ReceiptsInnerMessage69>.GetLength(ReceiptsInnerMessage69 message) =>
        GetLength(message);

    int IMessageSerializer<ReceiptsInnerMessage69>.Serialize(Span<byte> destination, ReceiptsInnerMessage69 message) =>
        Serialize(destination, message);

    ReceiptsInnerMessage69 IMessageSerializer<ReceiptsInnerMessage69>.Deserialize(ReadOnlySpan<byte> data)
    {
        ReceiptsMessage baseMessage = base.Deserialize(data);
        return new(baseMessage.TxReceipts);
    }
}
