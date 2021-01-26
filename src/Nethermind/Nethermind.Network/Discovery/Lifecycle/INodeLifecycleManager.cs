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

using System;
using System.Threading.Tasks;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Lifecycle
{
    public interface INodeLifecycleManager
    {
        Node ManagedNode { get; }
        INodeStats NodeStats { get; }
        NodeLifecycleState State { get; }
        void ProcessPingMessage(PingMessage discoveryMessage);
        void ProcessPongMessage(PongMessage discoveryMessage);
        void ProcessNeighborsMessage(NeighborsMessage discoveryMessage);
        void ProcessFindNodeMessage(FindNodeMessage discoveryMessage);
        void SendFindNode(byte[] searchedNodeId);
        Task SendPingAsync();
        void SendPong(PingMessage discoveryMessage);
        void SendNeighbors(Node[] nodes);

        void StartEvictionProcess();
        void LostEvictionProcess();
        event EventHandler<NodeLifecycleState> OnStateChanged;
    }
}
