// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

public class BlockAccessListsMessage66 : Eth66Message<BlockAccessListsMessage>
{
    public BlockAccessListsMessage66()
    {
    }

    public BlockAccessListsMessage66(long requestId, BlockAccessListsMessage ethMessage)
        : base(requestId, ethMessage)
    {
    }

    public override string ToString() => $"BlockAccessLists66({RequestId}, {EthMessage})";
}
