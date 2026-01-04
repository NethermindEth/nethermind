// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
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

    /// <summary>
    /// Determines whether the discovery manager should initiate contact with a node at the specified IP address.
    /// </summary>
    /// <param name="address">The IP address of the node to evaluate for contact.</param>
    /// <returns><see langword="true"/> if the node at the given address should be contacted; otherwise, <see langword="false"/>.</returns>
    bool ShouldContact(IPAddress address);
    NodeRecord SelfNodeRecord { get; }
}
