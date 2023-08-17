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

        public OwnedBlockBodies? Bodies { get; set; }

        public BlockBodiesMessage()
        {
        }

        public BlockBodiesMessage(Block[] blocks)
        {
            BlockBody[] bodies = new BlockBody[blocks.Length];
            for (int i = 0; i < blocks.Length; i++)
            {
                bodies[i] = blocks[i]?.Body;
            }

            Bodies = new OwnedBlockBodies(bodies);
        }

        public BlockBodiesMessage(BlockBody?[] bodies)
        {
            Bodies = new OwnedBlockBodies(bodies);
        }

        public override string ToString() => $"{nameof(BlockBodiesMessage)}({Bodies?.Bodies?.Length ?? 0})";
    }
}
