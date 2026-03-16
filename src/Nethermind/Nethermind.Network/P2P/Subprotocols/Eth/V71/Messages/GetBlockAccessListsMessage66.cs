// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

public class GetBlockAccessListsMessage66 : Eth66Message<GetBlockAccessListsMessage>
{
    public GetBlockAccessListsMessage66()
    {
    }

    public GetBlockAccessListsMessage66(long requestId, GetBlockAccessListsMessage ethMessage)
        : base(requestId, ethMessage)
    {
    }

    public override string ToString() => $"GetBlockAccessLists66({RequestId}, {EthMessage})";
}
