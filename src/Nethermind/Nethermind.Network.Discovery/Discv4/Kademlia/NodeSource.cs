// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4.Kademlia;

public sealed class NodeSource(
    IKademlia<PublicKey, Node> kademlia,
    IKademliaDiscovery<PublicKey, Node> kademliaDiscovery,
    IKademliaAdapter discv4Adapter,
    IDiscoveryConfig discoveryConfig,
    KademliaConfig<Node> kademliaConfig,
    IEnrForkIdFilter enrForkIdFilter,
    ILogManager logManager)
    : IKademliaNodeSource
{
    private const int ChannelCapacity = 64;

    private readonly ILogger _logger = logManager.GetClassLogger<NodeSource>();
    private readonly Hash256 _currentNodeHash = kademliaConfig.CurrentNodeId.IdHash;
    private readonly int _recentNodeLimit = RecentNodeFilter.GetLimit(kademliaConfig.KSize, Hash256KademliaDistance.Instance.MaxDistance, ChannelCapacity);

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug($"Starting discover nodes");
        using CancellationTokenSource disposeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken discoveryToken = disposeCts.Token;
        Channel<Node> ch = Channel.CreateBounded<Node>(ChannelCapacity);
        RecentNodeFilter<ValueHash256> recentlyWrittenNodes = new(_recentNodeLimit);

        async Task DiscoverAsync()
        {
            try
            {
                await foreach (Node node in kademliaDiscovery.DiscoverNodes(discoveryConfig.ConcurrentDiscoveryJob, ChannelCapacity, discoveryToken))
                {
                    await WriteDiscoveredNode(node);
                }
            }
            catch (OperationCanceledException) when (discoveryToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error("Kademlia discovery node stream failed.", ex);
            }
        }

        Task discoverTask = DiscoverAsync();

        try
        {
            kademlia.OnNodeAdded += Handler;

            await foreach (Node node in ch.Reader.ReadAllAsync(token))
            {
                yield return node;
            }
        }
        finally
        {
            kademlia.OnNodeAdded -= Handler;
            await disposeCts.CancelAsync();
            ch.Writer.TryComplete();
            try
            {
                await discoverTask;
            }
            catch (OperationCanceledException) when (discoveryToken.IsCancellationRequested)
            {
            }
        }

        yield break;

        async Task WriteDiscoveredNode(Node node)
        {
            if (IsExcluded(node) || !IsForkIdAcceptable(node))
            {
                return;
            }

            if (!discv4Adapter.GetSession(node).HasReceivedPong)
            {
                if (discv4Adapter.GetSession(node).HasTriedPingRecently)
                {
                    return;
                }

                if (!await discv4Adapter.Ping(node, discoveryToken))
                {
                    return;
                }
            }

            if (!recentlyWrittenNodes.TryReserve(node.IdHash))
            {
                return;
            }

            try
            {
                await ch.Writer.WriteAsync(node, discoveryToken);
            }
            catch
            {
                recentlyWrittenNodes.Release(node.IdHash);
                throw;
            }
        }

        void Handler(object? _, Node addedNode)
        {
            if (IsExcluded(addedNode) || !IsForkIdAcceptable(addedNode))
            {
                return;
            }

            if (!recentlyWrittenNodes.TryReserve(addedNode.IdHash))
            {
                return;
            }

            if (ch.Writer.TryWrite(addedNode))
            {
                return;
            }

            recentlyWrittenNodes.Release(addedNode.IdHash);
        }
    }

    private bool IsExcluded(Node node) => node.IdHash.Equals(_currentNodeHash);

    private bool IsForkIdAcceptable(Node node)
    {
        if (node.Enr is not { } record)
        {
            return true;
        }

        try
        {
            return enrForkIdFilter.IsAcceptable(record);
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to parse discv4 discovered ENR for {node}: {e}");
            return false;
        }
    }
}
