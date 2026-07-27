// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Xdc.Discovery;

/// <summary>
/// XDC's discv4 fork repurposed the standard ENR request/response type bytes for its own ping,
/// so there is no wire-compatible way to fetch a remote node's ENR. Remote ENR refresh is
/// disabled entirely instead of racing XDC nodes with requests they will never answer.
/// </summary>
public sealed class XdcKademliaAdapter(
    Lazy<IKademlia<PublicKey, Node>> kademlia,
    Lazy<INodeHealthTracker<Node>> nodeHealthTracker,
    IDiscoveryConfig discoveryConfig,
    KademliaConfig<Node> kademliaConfig,
    INodeRecordProvider nodeRecordProvider,
    INodeStatsManager nodeStatsManager,
    ITimestamper timestamper,
    IProcessExitSource processExitSource,
    IEcdsa ecdsa,
    ILogManager logManager)
    : KademliaAdapter(kademlia, nodeHealthTracker, discoveryConfig, kademliaConfig, nodeRecordProvider, nodeStatsManager, timestamper, processExitSource, ecdsa, logManager)
{
    protected override Task RefreshRemoteRecordIfNewer(Node node, ulong? advertisedSequence, CancellationToken token)
        => Task.CompletedTask;
}
