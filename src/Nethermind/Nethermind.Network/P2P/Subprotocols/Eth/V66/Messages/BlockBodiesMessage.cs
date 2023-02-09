// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class BlockBodiesMessage : Eth66Message<V62.Messages.BlockBodiesMessage>
    {
        public BlockBodiesMessage()
        {
        }

        public BlockBodiesMessage(long requestId, V62.Messages.BlockBodiesMessage ethMessage) : base(requestId, ethMessage)
        {
        }
    }
}
