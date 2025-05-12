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

namespace Nethermind.Network.Discovery.Discv4;

// TODO: Unit test, remove metric
public class KademliaNodeSource(
    IKademlia<PublicKey, Node> kademlia,
    IITeratorAlgo<Node> lookup2,
    KademliaDiscv4Adapter discv4Adapter,
    IDiscoveryConfig discoveryConfig,
    ILogManager logManager
)
{
    ILogger _logger = logManager.GetClassLogger();

    private Counter _discoverRound = Prometheus.Metrics.CreateCounter("kademlia_discover_rounds", "discovery rounds");
    private Counter _discoverPingResult = Prometheus.Metrics.CreateCounter("kademlia_discover_ping", "discovery rounds", "result");

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug($"Starting discover nodes");
        Channel<Node> ch = Channel.CreateBounded<Node>(64);
        ConcurrentDictionary<ValueHash256, ValueHash256> _writtenNodes = new();
        int duplicated = 0;
        int total = 0;

        async Task DiscoverAsync(PublicKey target)
        {
            _discoverRound.Inc();
            if (_logger.IsDebug) _logger.Debug($"Looking up {target}");
            bool anyFound = false;
            int count = 0;

            ValueHash256 targetHash = target.Hash;
            Func<Node, CancellationToken, Task<Node[]>> lookupOp = (nextNode, token) =>
                discv4Adapter.FindNeighbours(nextNode, target, token);
            await foreach (var node in lookup2.Lookup(targetHash, 128, 1, lookupOp!, token))
            {
                try
                {
                    await discv4Adapter.Ping(node, token);
                    _discoverPingResult.WithLabels("ok").Inc();
                }
                catch (OperationCanceledException)
                {
                    _discoverPingResult.WithLabels("timeout").Inc();
                    continue;
                }

                anyFound = true;
                count++;
                total++;
                if (!_writtenNodes.TryAdd(node.IdHash, node.IdHash))
                {
                    duplicated++;
                    continue;
                }
                await ch.Writer.WriteAsync(node, token);
            }

            if (!anyFound)
            {
                if (_logger.IsDebug) _logger.Debug($"No node found for {target}");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Found {count} nodes");
            }
        }

        Task discoverTask = Task.WhenAll(Enumerable.Range(0, discoveryConfig.ConcurrentDiscoveryJob).Select((_) => Task.Run(async () =>
        {
            Random random = new();
            byte[] randomBytes = new byte[64];
            while (!token.IsCancellationRequested)
            {
                Stopwatch iterationTime = Stopwatch.StartNew();

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
            kademlia.OnNodeAdded += Handler;

            await foreach (Node node in ch.Reader.ReadAllAsync(token))
            {
                yield return node;
            }
        }
        finally
        {
            await discoverTask;
            kademlia.OnNodeAdded -= Handler;
        }

        yield break;

        void Handler(object? _, Node addedNode)
        {
            _writtenNodes.TryAdd(addedNode.IdHash, addedNode.IdHash);
            ch.Writer.TryWrite(addedNode); // Ignore if channel full
        }
    }
}
