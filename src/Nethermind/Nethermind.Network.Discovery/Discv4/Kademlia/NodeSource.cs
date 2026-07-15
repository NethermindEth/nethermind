// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4.Kademlia;

public sealed class NodeSource(
    IKademliaDiscovery<PublicKey, Node> kademliaDiscovery,
    IKademliaAdapter discv4Adapter,
    IDiscoveryConfig discoveryConfig,
    KademliaConfig<Node> kademliaConfig,
    IForkInfo forkInfo,
    ILogManager logManager)
    : IKademliaNodeSource
{
    private const int ChannelCapacity = 64;

    private readonly ILogger _logger = logManager.GetClassLogger<NodeSource>();
    private readonly Hash256 _currentNodeHash = kademliaConfig.CurrentNodeId.IdHash;

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug("Starting discv4 node source");

        using CancellationTokenSource disposeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken discoveryToken = disposeCts.Token;

        Task discoverTask = DiscoverAsync();
        try
        {
            await foreach (Node node in discv4Adapter.ReadDiscoveredNodes(token))
            {
                if (!IsExcluded(node))
                {
                    yield return node;
                }
            }
        }
        finally
        {
            await disposeCts.CancelAsync();
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
                await foreach (Node _ in kademliaDiscovery.DiscoverNodes(
                    discoveryConfig.ConcurrentDiscoveryJob,
                    ChannelCapacity,
                    discoveryToken))
                {
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
    }

    private bool IsExcluded(Node node) => node.IdHash.Equals(_currentNodeHash) || HasIncompatibleForkId(node);

    private bool HasIncompatibleForkId(Node node)
    {
        try
        {
            return !forkInfo.IsNodeRecordForkCompatible(node.Enr);
        }
        catch (Exception e)
        {
            // Keep the candidate if local validation fails; discv4 callers have no per-node exception guard.
            if (_logger.IsTrace) _logger.Trace($"Unable to validate fork ID of discv4 discovered node {node:s}: {e}");
            return false;
        }
    }
}
