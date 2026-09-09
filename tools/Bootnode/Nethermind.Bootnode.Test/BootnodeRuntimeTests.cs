// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Bootnode.Test;

[TestFixture]
public class BootnodeRuntimeTests
{
    [Test]
    public async Task Uses_source_protocol_for_discovery_and_removal()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        Node node = new(privateKey.PublicKey, "127.0.0.1", 30303)
        {
            Enr = new NodeRecord()
        };
        TestNodeSource discv4Source = new(node);
        TestNodeSource discv5Source = new(node);
        DiscoveredNodeStore store = new();
        await using BootnodeRuntime runtime = new(
            new TestDiscoveryApp(),
            [
                new BootnodeDiscoverySource("discv4", discv4Source),
                new BootnodeDiscoverySource("discv5", discv5Source)
            ],
            store,
            new BootnodeMetrics(),
            new BootnodeKademliaBucketRegistry(),
            new ProcessExitSource(CancellationToken.None),
            LimboLogs.Instance);

        await runtime.StartAsync(CancellationToken.None);

        await Task
            .WhenAll(discv4Source.ObservationProcessed, discv5Source.ObservationProcessed)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(store.GetProtocol(node), Is.EqualTo("both"));

        discv4Source.Remove(node);
        Assert.That(store.CreateSnapshot().ActiveCount, Is.EqualTo(1));

        discv5Source.Remove(node);
        Assert.That(store.CreateSnapshot().ActiveCount, Is.Zero);
    }

    private sealed class TestNodeSource(Node node) : INodeSource
    {
        private readonly TaskCompletionSource _observationProcessed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ObservationProcessed => _observationProcessed.Task;

        public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return node;
            _observationProcessed.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public void Remove(Node removedNode) => NodeRemoved?.Invoke(this, new NodeEventArgs(removedNode));

        public event EventHandler<NodeEventArgs>? NodeRemoved;
    }

    private sealed class TestDiscoveryApp : IDiscoveryApp
    {
        public string Description => "test discovery";

        public void InitializeChannel(IChannel channel)
        {
        }

        public Task StartAsync() => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public void AddNodeToDiscovery(Node node)
        {
        }

        public IAsyncEnumerable<Node> DiscoverNodes(CancellationToken cancellationToken) => AsyncEnumerable.Empty<Node>();

        public event EventHandler<NodeEventArgs>? NodeRemoved
        {
            add { }
            remove { }
        }
    }
}
