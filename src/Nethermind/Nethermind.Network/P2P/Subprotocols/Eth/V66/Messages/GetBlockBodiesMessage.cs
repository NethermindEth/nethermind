// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetBlockBodiesMessage : Eth66Message<V62.Messages.GetBlockBodiesMessage>
    {
        public GetBlockBodiesMessage()
        {
        }

        public GetBlockBodiesMessage(long requestId, V62.Messages.GetBlockBodiesMessage ethMessage) : base(requestId, ethMessage)
        {
        }
    }
}
