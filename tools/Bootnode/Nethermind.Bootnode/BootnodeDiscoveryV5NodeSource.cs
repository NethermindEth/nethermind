// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv5.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Bootnode;

internal sealed class BootnodeDiscoveryV5NodeSource(
    IKademlia<PublicKey, Node> kademlia,
    IKademliaDiscovery<PublicKey, Node> kademliaDiscovery,
    IDiscoveryConfig discoveryConfig,
    KademliaConfig<Node> kademliaConfig,
    IDiscv5RecordFilter recordFilter,
    ILogManager logManager)
    : IKademliaNodeSource
{
    private const int ChannelCapacity = 64;
    private const int MaxBucketSizeForRecentLimit = 16;
    private const int MaxDiscoveryDistance = 256;

    private readonly Nethermind.Logging.ILogger _logger = logManager.GetClassLogger<BootnodeDiscoveryV5NodeSource>();
    private readonly Hash256 _currentNodeHash = kademliaConfig.CurrentNodeId.IdHash;
    private readonly int _recentNodeLimit = Math.Max(ChannelCapacity, Math.Min(kademliaConfig.KSize, MaxBucketSizeForRecentLimit) * MaxDiscoveryDistance);

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug("Starting bootnode discv5 node source");

        Channel<Node> channel = Channel.CreateBounded<Node>(ChannelCapacity);
        RecentNodeFilter<Hash256> recentlyWrittenNodes = new(_recentNodeLimit);
        int initialNodes = 0;
        using CancellationTokenSource disposeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken discoveryToken = disposeCts.Token;

        foreach (Node node in kademlia.IterateNodes())
        {
            if (!IsExcluded(node) &&
                TryCreateDiscoveryCandidate(node, out Node? discoveryCandidate) &&
                recentlyWrittenNodes.TryReserve(discoveryCandidate.IdHash))
            {
                initialNodes++;
                yield return discoveryCandidate;
            }
        }

        if (_logger.IsDebug) _logger.Debug($"Bootnode discv5 node source emitted {initialNodes} initial nodes from the routing table.");

        Task discoverTask = DiscoverAsync();
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
            await disposeCts.CancelAsync();
            channel.Writer.TryComplete();
            try
            {
                await discoverTask;
            }
            catch (OperationCanceledException) when (discoveryToken.IsCancellationRequested)
            {
            }
        }

        async Task DiscoverAsync()
        {
            try
            {
                await foreach (Node node in kademliaDiscovery.DiscoverNodes(discoveryConfig.ConcurrentDiscoveryJob, ChannelCapacity, discoveryToken))
                {
                    if (!TryReserveDiscoveryCandidate(node, out Node? discoveryCandidate))
                    {
                        continue;
                    }

                    try
                    {
                        await channel.Writer.WriteAsync(discoveryCandidate, discoveryToken);
                    }
                    catch
                    {
                        recentlyWrittenNodes.Release(discoveryCandidate.IdHash);
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (discoveryToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error("Bootnode discv5 Kademlia discovery node stream failed.", ex);
            }
        }

        void Handler(object? _, Node node)
        {
            if (!TryReserveDiscoveryCandidate(node, out Node? discoveryCandidate))
            {
                return;
            }

            if (channel.Writer.TryWrite(discoveryCandidate))
            {
                if (_logger.IsDebug) _logger.Debug($"Bootnode discv5 node source queued discovered node {discoveryCandidate:s}.");
                return;
            }

            recentlyWrittenNodes.Release(discoveryCandidate.IdHash);
            if (_logger.IsTrace)
            {
                _logger.Trace($"Bootnode discv5 node source queue is full, dropping discovered node {node:s}.");
            }
        }

        bool TryReserveDiscoveryCandidate(Node node, [NotNullWhen(true)] out Node? discoveryCandidate)
        {
            discoveryCandidate = null;
            if (IsExcluded(node) ||
                !TryCreateDiscoveryCandidate(node, out Node? candidate) ||
                !recentlyWrittenNodes.TryReserve(candidate.IdHash))
            {
                return false;
            }

            discoveryCandidate = candidate;
            return true;
        }
    }

    private bool IsExcluded(Node node) => node.IdHash.Equals(_currentNodeHash);

    private bool TryCreateDiscoveryCandidate(Node discoveryNode, [NotNullWhen(true)] out Node? discoveryCandidate)
    {
        discoveryCandidate = null;
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

            return Node.TryFromDiscoveryEnr(record, out discoveryCandidate);
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to parse bootnode discv5 discovered ENR for {discoveryNode}: {e}");
            return false;
        }
    }

    private sealed class RecentNodeFilter<TKey>(int maxCount)
        where TKey : notnull
    {
        private readonly Dictionary<TKey, long> _nodes = new(maxCount);
        private readonly Lock _lock = new();
        private Queue<(TKey NodeId, long Generation)> _recentNodes = new(maxCount);
        private long _generation;

        public bool TryReserve(TKey nodeId)
        {
            lock (_lock)
            {
                if (_nodes.ContainsKey(nodeId))
                {
                    return false;
                }

                long generation = unchecked(++_generation);
                _nodes.Add(nodeId, generation);
                _recentNodes.Enqueue((nodeId, generation));
                Trim();

                return true;
            }
        }

        public void Release(TKey nodeId)
        {
            lock (_lock)
            {
                _nodes.Remove(nodeId);
                DropReleasedHeadEntries();
                if (_recentNodes.Count > Math.Max(maxCount * 2, 256))
                {
                    CompactQueue();
                }
            }
        }

        private void Trim()
        {
            DropReleasedHeadEntries();
            while (_nodes.Count > maxCount && _recentNodes.TryDequeue(out (TKey NodeId, long Generation) oldestNode))
            {
                if (_nodes.TryGetValue(oldestNode.NodeId, out long generation) && generation == oldestNode.Generation)
                {
                    _nodes.Remove(oldestNode.NodeId);
                }
            }
        }

        private void DropReleasedHeadEntries()
        {
            while (_recentNodes.TryPeek(out (TKey NodeId, long Generation) oldestNode) &&
                   (!_nodes.TryGetValue(oldestNode.NodeId, out long generation) || generation != oldestNode.Generation))
            {
                _recentNodes.Dequeue();
            }
        }

        private void CompactQueue()
        {
            Queue<(TKey NodeId, long Generation)> compacted = new(_nodes.Count);
            foreach ((TKey NodeId, long Generation) node in _recentNodes)
            {
                if (_nodes.TryGetValue(node.NodeId, out long generation) && generation == node.Generation)
                {
                    compacted.Enqueue(node);
                }
            }

            _recentNodes = compacted;
        }
    }
}
