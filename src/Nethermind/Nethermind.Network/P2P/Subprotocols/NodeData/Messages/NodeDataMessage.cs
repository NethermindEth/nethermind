// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Network.P2P.Subprotocols.NodeData.Messages;

public class NodeDataMessage : Eth.V63.Messages.NodeDataMessage
{
    public override int PacketType { get; } = NodeDataMessageCode.NodeData;
    public override string Protocol { get; } = "nodedata";

    public NodeDataMessage(IOwnedReadOnlyList<byte[]>? data)
        : base(data)
    {
    }
}
