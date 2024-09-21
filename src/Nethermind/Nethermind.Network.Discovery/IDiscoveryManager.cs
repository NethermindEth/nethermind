// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Caching;
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
    Task<bool> WasMessageReceived(Hash256 senderIdHash, MsgType msgType, int timeout);
    event EventHandler<NodeEventArgs> NodeDiscovered;

    IReadOnlyCollection<INodeLifecycleManager> GetNodeLifecycleManagers();
    IReadOnlyCollection<INodeLifecycleManager> GetOrAddNodeLifecycleManagers(Func<INodeLifecycleManager, bool> query);
    ClockKeyCache<IpAddressAsKey> NodesFilter { get; }
    NodeRecord SelfNodeRecord { get; }

    public readonly struct IpAddressAsKey(IPAddress ipAddress) : IEquatable<IpAddressAsKey>
    {
        private readonly IPAddress _ipAddress = ipAddress;
        public static implicit operator IpAddressAsKey(IPAddress ip) => new(ip);
        public bool Equals(IpAddressAsKey other) => _ipAddress.Equals(other._ipAddress);
        public override bool Equals(object? obj) => obj is IpAddressAsKey ip && _ipAddress.Equals(ip._ipAddress);
        public override int GetHashCode() => _ipAddress.GetHashCode();
    }
}
