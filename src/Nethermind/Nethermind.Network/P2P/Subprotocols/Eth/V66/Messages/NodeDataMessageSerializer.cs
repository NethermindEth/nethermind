// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class NodeDataMessageSerializer : Eth66MessageSerializer<NodeDataMessage, V63.Messages.NodeDataMessage>
    {
        public NodeDataMessageSerializer() : base(new V63.Messages.NodeDataMessageSerializer())
        {
        }
    }
}
