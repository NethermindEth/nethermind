// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Network.P2P.Subprotocols.NodeData.Messages;

public class NodeDataMessage(IOwnedReadOnlyList<byte[]>? data) : Eth.V63.Messages.NodeDataMessage(data)
{
    public override int PacketType => NodeDataMessageCode.NodeData;
    public override string Protocol => "nodedata";
}
