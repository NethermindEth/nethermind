// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
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
    IIteratorNodeLookup<PublicKey, Node> lookup,
    IKademliaAdapter discv4Adapter,
    IDiscoveryConfig discoveryConfig,
    KademliaConfig<Node> kademliaConfig,
    ILogManager logManager)
    : IKademliaNodeSource
{
    private const int ChannelCapacity = 64;

    private readonly ILogger _logger = logManager.GetClassLogger<NodeSource>();
    private readonly int _recentNodeLimit = RecentNodeFilter.GetLimit(kademliaConfig.KSize, Hash256KademliaDistance.Instance.MaxDistance, ChannelCapacity);

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug($"Starting discover nodes");
        using CancellationTokenSource disposeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken discoveryToken = disposeCts.Token;
        Channel<Node> ch = Channel.CreateBounded<Node>(ChannelCapacity);
        RecentNodeFilter<ValueHash256> recentlyWrittenNodes = new(_recentNodeLimit);
        int duplicated = 0;
        int total = 0;

        async Task DiscoverAsync(PublicKey target)
        {
            if (_logger.IsDebug) _logger.Debug($"Looking up {target}");
            bool anyFound = false;
            int count = 0;

            await foreach (Node node in lookup.Lookup(target, discoveryToken))
            {
                if (!discv4Adapter.GetSession(node).HasReceivedPong)
                {
                    if (discv4Adapter.GetSession(node).HasTriedPingRecently)
                    {
                        // Tried ping before and did not receive a response
                        continue;
                    }
                    if (!await discv4Adapter.Ping(node, discoveryToken))
                    {
                        continue;
                    }
                }

                anyFound = true;
                count++;
                total++;
                if (!recentlyWrittenNodes.TryReserve(node.IdHash))
                {
                    duplicated++;
                    continue;
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

            if (!anyFound)
            {
                if (_logger.IsDebug) _logger.Debug($"No node found for {target}");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Found {count} nodes");
            }
        }

        Task[] discoverTasks = new Task[discoveryConfig.ConcurrentDiscoveryJob];
        for (int i = 0; i < discoverTasks.Length; i++)
        {
            discoverTasks[i] = Task.Run(async () =>
            {
                Random random = new();
                byte[] randomBytes = new byte[PublicKey.LengthInBytes];
                while (!discoveryToken.IsCancellationRequested)
                {
                    Stopwatch iterationTime = Stopwatch.StartNew();

                    try
                    {
                        random.NextBytes(randomBytes);
                        await DiscoverAsync(new PublicKey(randomBytes));

                        // Prevent high CPU when all node is not reachable due to network connectivity issue.
                        if (iterationTime.Elapsed < TimeSpan.FromSeconds(1))
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), discoveryToken);
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
            });
        }

        Task discoverTask = Task.WhenAll(discoverTasks);

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

        void Handler(object? _, Node addedNode)
        {
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
}
