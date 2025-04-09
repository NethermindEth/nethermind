// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

public interface IDiscoveryManager : IDiscoveryMsgListener
{
    IMsgSender MsgSender { set; }
    INodeLifecycleManager? GetNodeLifecycleManager(Node node, bool isPersisted = false);
    void SendMessage(DiscoveryMsg discoveryMsg);
    Task SendMessageAsync(DiscoveryMsg discoveryMsg);
    ValueTask<bool> WasMessageReceived(Hash256 senderIdHash, MsgType msgType, int timeout);
    event EventHandler<NodeEventArgs> NodeDiscovered;

    IReadOnlyCollection<INodeLifecycleManager> GetNodeLifecycleManagers();
    IReadOnlyCollection<INodeLifecycleManager> GetOrAddNodeLifecycleManagers(Func<INodeLifecycleManager, bool> query);
    NodeFilter NodesFilter { get; }
    NodeRecord SelfNodeRecord { get; }
}
