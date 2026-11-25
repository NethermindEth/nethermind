// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

public class CompositeNodeSourceTests
{
    [Test]
    public async Task DiscoverNodes_ShouldComplete_WhenAllSourcesFinish([Values(0, 1, 2)] int sourceCount)
    {
        Node node = new(TestItem.PublicKeyA, "1.2.3.4", 1234);
        INodeSource[] sources = Enumerable.Range(0, sourceCount).Select(_ => Substitute.For<INodeSource>()).ToArray();
        foreach (INodeSource source in sources)
        {
            source.DiscoverNodes(Arg.Any<CancellationToken>()).Returns(new[] { node }.ToAsyncEnumerable());
        }

        using CompositeNodeSource compositeNodeSource = new(sources);
        List<Node> nodes = await compositeNodeSource.DiscoverNodes(CancellationToken.None)
            .ToListAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(nodes, Is.EqualTo(Enumerable.Repeat(node, sourceCount)));
    }

    [Test]
    public async Task DiscoverNodes_ShouldPropagateSourceFailure_WhileAnotherSourceIsRunning()
    {
        Node node = new(TestItem.PublicKeyA, "1.2.3.4", 1234);
        INodeSource failingSource = Substitute.For<INodeSource>();
        failingSource.DiscoverNodes(Arg.Any<CancellationToken>()).Returns(YieldThenThrow(node));
        using CompositeNodeSource compositeNodeSource = new(failingSource, new TestNodeSource());
        await using IAsyncEnumerator<Node> enumerator = compositeNodeSource.DiscoverNodes(CancellationToken.None).GetAsyncEnumerator();

        bool hasNode = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Node firstNode = enumerator.Current;
        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hasNode, Is.True);
            Assert.That(firstNode, Is.EqualTo(node));
            Assert.That(exception?.Message, Is.EqualTo("Node source failed"));
        }
    }

    [Test]
    public void DiscoverNodes_ShouldStop_WhenCancelled()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        using CompositeNodeSource compositeNodeSource = new(new TestNodeSource());
        Task<List<Node>> enumeration = compositeNodeSource.DiscoverNodes(cancellationTokenSource.Token).ToListAsync().AsTask();

        cancellationTokenSource.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await enumeration.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public void CompositeNodeSource_ShouldIgnoreNodeRemoved_AfterDispose()
    {
        TestNodeSource innerSource = new();
        CompositeNodeSource compositeNodeSource = new(innerSource);
        Node? removedNode = null;
        compositeNodeSource.NodeRemoved += (_, args) => removedNode = args.Node;

        compositeNodeSource.Dispose();
        innerSource.RemoveNode(new Node(TestItem.PublicKeyA, "1.2.3.4", 1234));

        Assert.That(removedNode, Is.Null);
    }

    [Test]
    public async Task DiscoverNodes_ShouldStopInnerSources_WhenEnumerationIsDisposed()
    {
        TestNodeSource innerSource = new();
        CompositeNodeSource compositeNodeSource = new(innerSource);
        Node node = new(TestItem.PublicKeyA, "1.2.3.4", 1234);
        innerSource.AddNode(node);

        Task<List<Node>> enumeration = compositeNodeSource.DiscoverNodes(CancellationToken.None).Take(1).ToListAsync().AsTask();
        List<Node> nodes = await enumeration.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(nodes, Is.EqualTo(new[] { node }));
    }

    private static async IAsyncEnumerable<Node> YieldThenThrow(Node node)
    {
        yield return node;
        await Task.Yield();
        throw new InvalidOperationException("Node source failed");
    }
}
