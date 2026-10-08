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
    IZeroMessageSerializer<ReceiptsInnerMessage69>
{
    int IZeroMessageSerializer<ReceiptsInnerMessage69>.GetLength(ReceiptsInnerMessage69 message, out int contentLength) =>
        GetLength(message, out contentLength);

    void IZeroMessageSerializer<ReceiptsInnerMessage69>.Serialize(Span<byte> buffer, ReceiptsInnerMessage69 message) =>
        Serialize(buffer, message);

    ReceiptsInnerMessage69 IZeroMessageSerializer<ReceiptsInnerMessage69>.Deserialize(ReadOnlySpan<byte> data, out int consumed)
    {
        ReceiptsMessage baseMessage = base.Deserialize(data, out consumed);
        return new(baseMessage.TxReceipts);
    }
}
