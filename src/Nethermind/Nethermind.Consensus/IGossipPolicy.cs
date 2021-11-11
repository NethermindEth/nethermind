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
// 

namespace Nethermind.Consensus
{
    public interface IGossipPolicy
    {
        public bool ShouldGossipBlocks { get; }

        public bool ShouldDisconnectGossipingNodes { get; }
    }

    internal class ShouldNotGossip : IGossipPolicy
    {
        private ShouldNotGossip() { }

        public static ShouldNotGossip Instance { get; } = new ();
        
        public bool ShouldGossipBlocks => false;
        public bool ShouldDisconnectGossipingNodes => false;
    }
    
    internal class ShouldGossip : IGossipPolicy
    {
        private ShouldGossip() { }

        public static IGossipPolicy Instance { get; } = new ShouldGossip();
        
        public bool ShouldGossipBlocks => true;
        public bool ShouldDisconnectGossipingNodes => true;
    }
    
    public static class Policy
    {
        public static IGossipPolicy NoBlockGossip { get; } = ShouldNotGossip.Instance;
        
        public static IGossipPolicy FullGossip { get; } = ShouldGossip.Instance;
    }
}
