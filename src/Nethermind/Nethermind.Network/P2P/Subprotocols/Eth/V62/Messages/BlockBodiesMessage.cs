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

        public UnmanagedBlockBodies Bodies;

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

            Bodies = new UnmanagedBlockBodies(bodies);
        }

        public BlockBodiesMessage(BlockBody?[] bodies)
        {
            Bodies = new UnmanagedBlockBodies(bodies);
        }

        public BlockBodiesMessage(UnmanagedBlockBodies bodies)
        {
            Bodies = bodies;
        }

        public override string ToString() => $"{nameof(BlockBodiesMessage)}({-1})";
    }
}
