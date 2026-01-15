// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class ReceiptsMessage70 : Eth66Message<V63.Messages.ReceiptsMessage>
{
    public bool LastBlockIncomplete { get; set; }

    public ReceiptsMessage70()
    {
    }

    public ReceiptsMessage70(long requestId, V63.Messages.ReceiptsMessage ethMessage, bool lastBlockIncomplete)
        : base(requestId, ethMessage)
    {
        LastBlockIncomplete = lastBlockIncomplete;
    }

    public override string ToString() => $"Receipts70({RequestId}, incomplete={LastBlockIncomplete}, {EthMessage})";
}
