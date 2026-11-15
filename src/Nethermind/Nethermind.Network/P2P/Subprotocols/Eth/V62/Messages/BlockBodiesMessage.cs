// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockBodiesMessage : P2PMessage
    {
        public override int PacketType => Eth62MessageCode.BlockBodies;
        public override string Protocol => "eth";

        public OwnedBlockBodies? Bodies { get; set; }

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

            Bodies = new OwnedBlockBodies(bodies);
        }

        public BlockBodiesMessage(BlockBody?[] bodies) => Bodies = new OwnedBlockBodies(bodies);

        /// <summary>Creates a message from <paramref name="count"/> bodies already encoded back to back, and takes ownership of the buffer.</summary>
        public BlockBodiesMessage(ArrayPoolList<byte> encodedBodies, int count)
        {
            EncodedBodies = encodedBodies;
            EncodedBodyCount = count;
        }

        /// <summary>The bodies in their network encoding, back to back, when the message was built for sending.</summary>
        public ArrayPoolList<byte>? EncodedBodies { get; }

        public int EncodedBodyCount { get; }

        public override void Dispose()
        {
            base.Dispose();
            Bodies?.Dispose();
            EncodedBodies?.Dispose();
        }

        public override string ToString() => $"{nameof(BlockBodiesMessage)}({Bodies?.Bodies?.Length ?? EncodedBodyCount})";
    }
}
