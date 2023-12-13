// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class BlockBodiesMessageSerializer : Eth66MessageSerializer<BlockBodiesMessage, V62.Messages.BlockBodiesMessage>
    {
        public BlockBodiesMessageSerializer() : base(new V62.Messages.BlockBodiesMessageSerializer())
        {
        }
    }
}
