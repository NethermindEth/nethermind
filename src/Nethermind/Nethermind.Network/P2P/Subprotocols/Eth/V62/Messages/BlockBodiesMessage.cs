// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockBodiesMessage : P2PMessage
    {
        public override int PacketType { get; } = Eth62MessageCode.BlockBodies;
        public override string Protocol { get; } = "eth";

        public BlockBodiesMessage()
        {
        }

        public BlockBodiesMessage(Block[] blocks)
        {
            Bodies = new BlockBody[blocks.Length];
            for (int i = 0; i < blocks.Length; i++)
            {
                Bodies[i] = blocks[i] is null ? null : blocks[i].Body;
            }
        }

        public BlockBodiesMessage(BlockBody[] bodies)
        {
            Bodies = bodies;
        }

        public BlockBody[] Bodies { get; set; }

        public override string ToString() => $"{nameof(BlockBodiesMessage)}({Bodies?.Length ?? 0})";
    }
}
