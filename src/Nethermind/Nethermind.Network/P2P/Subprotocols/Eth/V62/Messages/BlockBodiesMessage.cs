// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockBodiesMessage : P2PMessage
    {
        public override int PacketType => Eth62MessageCode.BlockBodies;
        public override string Protocol => "eth";

        public RlpBlockBodies? Bodies { get; set; }

        public BlockBodiesMessage()
        {
        }

        public BlockBodiesMessage(IReadOnlyList<Block> blocks)
        {
            BlockBody[] bodies = new BlockBody[blocks.Count];
            for (int i = 0; i < blocks.Count; i++)
            {
                bodies[i] = blocks[i]?.Body;
            }

            Bodies = RlpBlockBodies.FromBodies(bodies);
        }

        public BlockBodiesMessage(BlockBody?[] bodies) => Bodies = RlpBlockBodies.FromBodies(bodies);

        public override void Dispose()
        {
            base.Dispose();
            Bodies?.Dispose();
        }

        public override string ToString() => $"{nameof(BlockBodiesMessage)}({Bodies?.Count ?? 0})";
    }
}
