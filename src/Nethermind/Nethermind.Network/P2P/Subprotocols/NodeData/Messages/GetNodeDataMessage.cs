// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.NodeData.Messages;

public class GetNodeDataMessage : Eth.V63.Messages.GetNodeDataMessage
{
    public override int PacketType { get; } = NodeDataMessageCode.GetNodeData;
    public override string Protocol { get; } = "nodedata";

    public GetNodeDataMessage(IOwnedReadOnlyList<Hash256> keys)
        : base(keys)
    {
    }
}
