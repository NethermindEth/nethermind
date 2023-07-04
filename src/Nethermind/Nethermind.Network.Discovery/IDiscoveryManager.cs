// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

public interface IDiscoveryManager : IDiscoveryMsgListener
{
    IMsgSender MsgSender { set; }
    INodeLifecycleManager? GetNodeLifecycleManager(Node node, bool isPersisted = false);
    void SendMessage(DiscoveryMsg discoveryMsg);
    Task<bool> WasMessageReceived(Keccak senderIdHash, MsgType msgType, int timeout);
    event EventHandler<NodeEventArgs> NodeDiscovered;

    IReadOnlyCollection<INodeLifecycleManager> GetNodeLifecycleManagers();
    IReadOnlyCollection<INodeLifecycleManager> GetOrAddNodeLifecycleManagers(Func<INodeLifecycleManager, bool> query);
}
