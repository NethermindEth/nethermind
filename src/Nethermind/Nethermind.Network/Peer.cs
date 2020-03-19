//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Network.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    /// <summary>
    /// Peer represents a connection state with another node of the P2P network.
    /// Because peers are actively searching for each other and initializing connections it may happen that
    /// the peers will simultaneously connect and we will have both incoming and outgoing connections to the same
    /// node.
    /// In such cases we manage the sessions by disconnecting one of the sessions and keeping the other.
    /// The logic for choosing which connection to drop has to be consistent between two peers - we use the PublicKey
    /// comparison to choose the connection direction in the same way on both nodes.
    /// </summary>
    public class Peer
    {
        public Peer(Node node)
        {
            Node = node;
        }

        /// <summary>
        /// 
        /// </summary>
        public Node Node { get; }
        public ISession InSession { get; set; }
        public ISession OutSession { get; set; }

        public override string ToString()
        {
            return $"[Peer|{Node:s}|{InSession}|{OutSession}]";
        }
    }
}