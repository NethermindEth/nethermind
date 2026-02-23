// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class GetReceiptsMessage70 : Eth66Message<V63.Messages.GetReceiptsMessage>
{
    public long FirstBlockReceiptIndex { get; set; }

    public GetReceiptsMessage70()
    {
    }

    public GetReceiptsMessage70(long requestId, long firstBlockReceiptIndex, V63.Messages.GetReceiptsMessage ethMessage)
        : base(requestId, ethMessage)
    {
        FirstBlockReceiptIndex = firstBlockReceiptIndex;
    }

    public override string ToString() => $"GetReceipts70({RequestId}, start={FirstBlockReceiptIndex}, {EthMessage})";
}
