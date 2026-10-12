// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5.Kademlia;

public sealed class NodeSource(
    IKademlia<PublicKey, Node> kademlia,
    IKademliaDiscovery<PublicKey, Node> kademliaDiscovery,
    IDiscoveryConfig discoveryConfig,
    KademliaConfig<Node> kademliaConfig,
    IDiscv5RecordFilter recordFilter,
    IForkInfo forkInfo,
    ILogManager logManager)
    : IKademliaNodeSource
{
    private const int ChannelCapacity = 64;

    private readonly ILogger _logger = logManager.GetClassLogger<NodeSource>();
    private readonly Hash256 _currentNodeHash = kademliaConfig.CurrentNodeId.IdHash;
    private readonly int _recentNodeLimit = RecentNodeFilter.GetLimit(kademliaConfig.KSize, ValueHash256KademliaDistance.Instance.MaxDistance, ChannelCapacity);

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug("Starting discv5 node source");

        Channel<Node> channel = Channel.CreateBounded<Node>(ChannelCapacity);
        RecentNodeFilter<Hash256> recentlyWrittenNodes = new(_recentNodeLimit);
        int initialNodes = 0;
        using CancellationTokenSource disposeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken discoveryToken = disposeCts.Token;

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
                    if (!TryReservePeerCandidate(node, out Node? peerCandidate))
                    {
                        continue;
                    }

                    try
                    {
                        await channel.Writer.WriteAsync(peerCandidate, discoveryToken);
                    }
                    catch
                    {
                        recentlyWrittenNodes.Release(peerCandidate.IdHash);
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (discoveryToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error("Discv5 Kademlia discovery node stream failed.", ex);
            }
        }

        void Handler(object? _, Node node)
        {
            if (!TryReservePeerCandidate(node, out Node? peerCandidate))
            {
                return;
            }

            if (channel.Writer.TryWrite(peerCandidate))
            {
                if (_logger.IsTrace) TraceQueuedDiscoveredNode(peerCandidate);
                return;
            }

            recentlyWrittenNodes.Release(peerCandidate.IdHash);
            if (_logger.IsTrace)
            {
                _logger.Trace($"Discv5 node source queue is full, dropping discovered node {node:s}.");
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceQueuedDiscoveredNode(Node candidate) =>
                _logger.Trace($"Discv5 node source queued discovered node {candidate:s}.");
        }

        bool TryReservePeerCandidate(Node node, [NotNullWhen(true)] out Node? peerCandidate)
        {
            peerCandidate = null;
            if (IsExcluded(node) ||
                !TryCreatePeerCandidate(node, out Node? candidate) ||
                !recentlyWrittenNodes.TryReserve(candidate.IdHash))
            {
                return false;
            }

            peerCandidate = candidate;
            return true;
        }
    }

    private bool IsExcluded(Node node) => node.IsBootnode || node.IdHash.Equals(_currentNodeHash);

    private bool TryCreatePeerCandidate(Node discoveryNode, [NotNullWhen(true)] out Node? peerCandidate)
    {
        peerCandidate = null;
        if (discoveryNode.Enr is not { Signature: not null } record)
        {
            return false;
        }

        try
        {
            if (record.EnrSequence < discoveryNode.HighestObservedEnrSequence)
            {
                if (_logger.IsTrace) _logger.Trace($"Skipping stale discv5 ENR peer candidate for {discoveryNode:s}.");
                return false;
            }

            if (recordFilter.Excludes(record))
            {
                return false;
            }

            if (!forkInfo.IsNodeRecordForkCompatible(record))
            {
                if (_logger.IsTrace) _logger.Trace($"Skipping discv5 discovered node {discoveryNode:s} with incompatible fork ID.");
                return false;
            }

            AddressFamily addressFamily = DiscoveryAddressSupport.GetFamily(discoveryNode.DiscoveryAddress.Address);
            // EIP-778 endpoints are independent: prefer the proven discovery family without
            // discarding a record whose only usable RLPx endpoint belongs to the other family.
            if (!Node.TryFromEnr(record, addressFamily, out peerCandidate) &&
                !Node.TryFromEnr(record, out peerCandidate))
            {
                return false;
            }

            if (discoveryNode.IsVerifiedEnr(record))
            {
                peerCandidate.SetVerifiedEnr(record);
            }

            peerCandidate.ObserveEnrSequence(discoveryNode.HighestObservedEnrSequence);
            return true;
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to parse discv5 discovered ENR for {discoveryNode}: {e}");
            return false;
        }
    }
}
