// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;
using Prometheus;

namespace Nethermind.Network.Discovery;

public class KademliaNodeSource(
    IKademlia<PublicKey, Node> kademlia,
    IITeratorAlgo<Node> _lookup2,
    KademliaDiscv4Adapter discv4Adapter,
    ILogManager logManager
)
{
    ILogger _logger = logManager.GetClassLogger();

    private Counter _kademliaDiscoveredNodes = Prometheus.Metrics.CreateCounter("kademlia_discovered_nodes", "Discovered");
    private Counter _kademliaDiscoveredNodeStatus = Prometheus.Metrics.CreateCounter("kademlia_discovered_nodes_status", "Discovered", "status");

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug($"Starting discover nodes");
        Channel<Node> ch = Channel.CreateBounded<Node>(64);
        ConcurrentDictionary<ValueHash256, ValueHash256> _writtenNodes = new();
        int duplicated = 0;
        int total = 0;

        void handler(object? _, Node addedNode)
        {
            _writtenNodes.TryAdd(addedNode.IdHash, addedNode.IdHash);
            ch.Writer.TryWrite(addedNode);
        }

        async Task DiscoverAsync(PublicKey target)
        {
            if (_logger.IsDebug) _logger.Debug($"Looking up {target}");
            bool anyFound = false;
            int count = 0;

            ValueHash256 targetHash = target.Hash;
            Func<Node, CancellationToken, Task<Node[]>> lookupOp = (nextNode, token) =>
                discv4Adapter.FindNeighbours(nextNode, target, token);
            await foreach (var node in _lookup2.Lookup(targetHash, 128, lookupOp!, token))
            {
                try
                {
                    await discv4Adapter.Ping(node, token);
                }
                catch (OperationCanceledException)
                {
                    _kademliaDiscoveredNodeStatus.WithLabels("ping_timeout").Inc();
                    continue;
                }

                _kademliaDiscoveredNodeStatus.WithLabels("ok").Inc();
                anyFound = true;
                count++;
                total++;
                if (!_writtenNodes.TryAdd(node.IdHash, node.IdHash))
                {
                    duplicated++;
                    continue;
                }
                _kademliaDiscoveredNodes.Inc();
                await ch.Writer.WriteAsync(node, token);
            }

            _logger.Warn($"Round found {count} nodes. Total is {total}");
            if (!anyFound)
            {
                if (_logger.IsDebug) _logger.Debug($"No node found for {target}");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Found {count} nodes");
            }
        }

        Task discoverTask = Task.WhenAll(Enumerable.Range(0, 6).Select((_) => Task.Run(async () =>
        {
            Random random = new();
            byte[] randomBytes = new byte[64];
            int iterationCount = 0;
            while (!token.IsCancellationRequested)
            {
                Stopwatch iterationTime = Stopwatch.StartNew();
                if (iterationCount % 10 == 0)
                {
                    // Probably shnould be done once or in a few interval
                    // await EnsureBootNodes(token);
                }

                try
                {
                    random.NextBytes(randomBytes);
                    await DiscoverAsync(new PublicKey(randomBytes));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"Discovery via custom random walk failed.", ex);
                }

                // Prevent high CPU when all node is not reachable due to network connectivity issue.
                if (iterationTime.Elapsed < TimeSpan.FromSeconds(1))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
            }
        })));

        try
        {
            kademlia.OnNodeAdded += handler;

            await foreach (Node node in ch.Reader.ReadAllAsync(token))
            {
                yield return node;
            }
        }
        finally
        {
            await discoverTask;
            kademlia.OnNodeAdded -= handler;
        }
    }
}
