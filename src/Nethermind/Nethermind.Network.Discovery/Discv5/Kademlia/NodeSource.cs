// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5.Kademlia;

public sealed class NodeSource(
    IKademlia<PublicKey, Node> kademlia,
    KademliaConfig<Node> kademliaConfig,
    IDiscv5RecordFilter recordFilter,
    ILogManager logManager)
    : IKademliaNodeSource
{
    private const int ChannelCapacity = 64;

    private readonly ILogger _logger = logManager.GetClassLogger<NodeSource>();
    private readonly Hash256 _currentNodeHash = kademliaConfig.CurrentNodeId.IdHash;
    private readonly int _recentNodeLimit = RecentNodeFilter.GetLimit(kademliaConfig.KSize, Hash256KademliaDistance.Instance.MaxDistance, ChannelCapacity);

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug("Starting discv5 node source");

        Channel<Node> channel = Channel.CreateBounded<Node>(ChannelCapacity);
        RecentNodeFilter<Hash256> recentlyWrittenNodes = new(_recentNodeLimit);
        int initialNodes = 0;

        foreach (Node node in kademlia.IterateNodes())
        {
            if (!IsExcluded(node) &&
                TryCreatePeerCandidate(node, out Node? peerCandidate) &&
                recentlyWrittenNodes.TryReserve(peerCandidate.IdHash))
            {
                initialNodes++;
                yield return peerCandidate;
            }
        }

        if (_logger.IsDebug) _logger.Debug($"Discv5 node source emitted {initialNodes} initial nodes from the routing table.");

        kademlia.OnNodeAdded += Handler;
        try
        {
            await foreach (Node node in channel.Reader.ReadAllAsync(token))
            {
                yield return node;
            }
        }
        finally
        {
            kademlia.OnNodeAdded -= Handler;
        }

        void Handler(object? _, Node node)
        {
            if (IsExcluded(node) ||
                !TryCreatePeerCandidate(node, out Node? peerCandidate) ||
                !recentlyWrittenNodes.TryReserve(peerCandidate.IdHash))
            {
                return;
            }

            if (channel.Writer.TryWrite(peerCandidate))
            {
                if (_logger.IsDebug) _logger.Debug($"Discv5 node source queued discovered node {peerCandidate:s}.");
                return;
            }

            recentlyWrittenNodes.Release(peerCandidate.IdHash);
            if (_logger.IsTrace)
            {
                _logger.Trace($"Discv5 node source queue is full, dropping discovered node {node:s}.");
            }
        }
    }

    private bool IsExcluded(Node node) => node.IsBootnode || node.IdHash.Equals(_currentNodeHash);

    private bool TryCreatePeerCandidate(Node discoveryNode, [NotNullWhen(true)] out Node? peerCandidate)
    {
        peerCandidate = null;
        if (string.IsNullOrEmpty(discoveryNode.Enr))
        {
            return false;
        }

        try
        {
            NodeRecord record = NodeRecord.FromEnrString(discoveryNode.Enr);
            if (recordFilter.Excludes(record))
            {
                return false;
            }

            return Node.TryFromEnr(record, out peerCandidate);
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to parse discv5 discovered ENR for {discoveryNode}: {e}");
            return false;
        }
    }
}
