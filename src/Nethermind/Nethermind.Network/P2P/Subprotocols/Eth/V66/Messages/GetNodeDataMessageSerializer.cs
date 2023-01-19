// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetNodeDataMessageSerializer : Eth66MessageSerializer<GetNodeDataMessage, V63.Messages.GetNodeDataMessage>
    {
        public GetNodeDataMessageSerializer() : base(new V63.Messages.GetNodeDataMessageSerializer())
        {
        }
    }
}
