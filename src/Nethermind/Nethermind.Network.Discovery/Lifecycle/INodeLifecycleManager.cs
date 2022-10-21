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

using Nethermind.Network.Discovery.Messages;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Lifecycle;

public interface INodeLifecycleManager
{
    Node ManagedNode { get; }
    INodeStats NodeStats { get; }
    NodeLifecycleState State { get; }
    void ProcessPingMsg(PingMsg pingMsg);
    void ProcessPongMsg(PongMsg pongMsg);
    void ProcessNeighborsMsg(NeighborsMsg msg);
    void ProcessFindNodeMsg(FindNodeMsg msg);
    void ProcessEnrRequestMsg(EnrRequestMsg enrRequestMessage);
    void ProcessEnrResponseMsg(EnrResponseMsg msg);
    void SendFindNode(byte[] searchedNodeId);
    Task SendPingAsync();

    void StartEvictionProcess();
    void LostEvictionProcess();
    event EventHandler<NodeLifecycleState> OnStateChanged;
}
