//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
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
                Bodies[i] = blocks[i] == null ? null : blocks[i].Body;
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
