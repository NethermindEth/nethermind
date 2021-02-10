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

using Nethermind.Network.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    /// <summary>
    /// Peer represents a connection state with another node of the P2P network.
    /// Because peers are actively searching for each other and initializing connections it may happen that
    /// two peers will simultaneously connect and we will have both incoming and outgoing connections to the same
    /// network node.
    /// In such cases we manage the sessions by disconnecting one of the sessions and keeping the other.
    /// The logic for choosing which session to drop has to be consistent between the two peers - we use the PublicKey
    /// comparison to choose the connection direction in the same way on both sides.
    /// </summary>
    public class Peer
    {
        public Peer(Node node)
        {
            Node = node;
        }

        public bool IsAwaitingConnection { get; set; }
        
        /// <summary>
        /// A physical network node with a network address combined with information about the client version
        /// and any extra attributes that we assign to a network node (static / trusted / bootnode).
        /// </summary>
        public Node Node { get; }
        
        /// <summary>
        /// An incoming session to the Node which can be in one of many states.
        /// </summary>
        public ISession? InSession { get; set; }
        
        /// <summary>
        /// An outgoing session to the Node which can be in one of many states.
        /// </summary>
        public ISession? OutSession { get; set; }

        public override string ToString()
        {
            return $"[Peer|{Node:s}|{InSession}|{OutSession}]";
        }
    }
}
