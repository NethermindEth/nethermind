// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetNodeDataMessage : Eth66Message<V63.Messages.GetNodeDataMessage>
    {
        public GetNodeDataMessage()
        {
        }

        public GetNodeDataMessage(long requestId, V63.Messages.GetNodeDataMessage ethMessage) : base(requestId, ethMessage)
        {
        }
    }
}
