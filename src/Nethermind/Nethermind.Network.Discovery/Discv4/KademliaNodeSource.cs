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

namespace Nethermind.Network.Discovery.Discv4;

public class KademliaNodeSource : IKademliaNodeSource
{
    private readonly IKademlia<PublicKey, Node> _kademlia;
    private readonly IIteratorNodeLookup _lookup;
    private readonly IKademliaDiscv4Adapter _discv4Adapter;
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly ILogger _logger;

    public KademliaNodeSource(
        IKademlia<PublicKey, Node> kademlia,
        IIteratorNodeLookup lookup2,
        IKademliaDiscv4Adapter discv4Adapter,
        IDiscoveryConfig discoveryConfig,
        ILogManager logManager)
    {
        _kademlia = kademlia;
        _lookup = lookup2;
        _discv4Adapter = discv4Adapter;
        _discoveryConfig = discoveryConfig;
        _logger = logManager.GetClassLogger();
    }

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug($"Starting discover nodes");
        Channel<Node> ch = Channel.CreateBounded<Node>(64);
        ConcurrentDictionary<ValueHash256, ValueHash256> writtenNodes = new();
        int duplicated = 0;
        int total = 0;

        async Task DiscoverAsync(PublicKey target)
        {
            if (_logger.IsDebug) _logger.Debug($"Looking up {target}");
            bool anyFound = false;
            int count = 0;

            await foreach (var node in _lookup.Lookup(target, token))
            {
                if (!_discv4Adapter.GetSession(node).HasReceivedPong)
                {
                    if (_discv4Adapter.GetSession(node).HasTriedPingRecently)
                    {
                        // Tried ping before and did not receive a response
                        continue;
                    }
                    try
                    {
                        await _discv4Adapter.Ping(node, token);
                    }
                    catch (OperationCanceledException)
                    {
                        continue;
                    }
                }

                anyFound = true;
                count++;
                total++;
                if (!writtenNodes.TryAdd(node.IdHash, node.IdHash))
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

        Task discoverTask = Task.WhenAll(Enumerable.Range(0, _discoveryConfig.ConcurrentDiscoveryJob).Select((_) => Task.Run(async () =>
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

                    // Prevent high CPU when all node is not reachable due to network connectivity issue.
                    if (iterationTime.Elapsed < TimeSpan.FromSeconds(1))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"Discovery via custom random walk failed.", ex);
                }
            }
        })));

        try
        {
            _kademlia.OnNodeAdded += Handler;

            await foreach (Node node in ch.Reader.ReadAllAsync(token))
            {
                yield return node;
            }
        }
        finally
        {
            await discoverTask;
            _kademlia.OnNodeAdded -= Handler;
        }

        yield break;

        void Handler(object? _, Node addedNode)
        {
            writtenNodes.TryAdd(addedNode.IdHash, addedNode.IdHash);
            ch.Writer.TryWrite(addedNode); // Ignore if channel full
        }
    }
}
