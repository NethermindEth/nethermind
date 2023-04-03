// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
