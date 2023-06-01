// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P;
using Nethermind.Stats;
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
    public sealed class Peer
    {
        public Peer(Node node) : this(node, null)
        { }

        public Peer(Node node, INodeStats stats)
        {
            Node = node;
            Stats = stats;
        }

        public bool IsAwaitingConnection { get; set; }

        /// <summary>
        /// A physical network node with a network address combined with information about the client version
        /// and any extra attributes that we assign to a network node (static / trusted / bootnode).
        /// </summary>
        public Node Node { get; }

        internal INodeStats Stats { get; }

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
