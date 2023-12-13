// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class NodeDataMessage : Eth66Message<V63.Messages.NodeDataMessage>
    {
        public NodeDataMessage()
        {
        }

        public NodeDataMessage(long requestId, V63.Messages.NodeDataMessage ethMessage) : base(requestId, ethMessage)
        {
        }
    }
}
