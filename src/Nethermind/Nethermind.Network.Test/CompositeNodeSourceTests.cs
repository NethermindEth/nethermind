// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

public class CompositeNodeSourceTests
{
    [Test]
    public async Task DiscoverNodes_Completes_after_finite_sources_including_faults([Values] bool fail)
    {
        INodeSource first = Substitute.For<INodeSource>();
        INodeSource second = Substitute.For<INodeSource>();
        Node node = new(TestItem.PublicKeyA, "1.2.3.4", 1234);
        first.DiscoverNodes(Arg.Any<CancellationToken>()).Returns(Enumerate(fail, null));
        second.DiscoverNodes(Arg.Any<CancellationToken>()).Returns(Enumerate(false, node));
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsError.Returns(true);
        ILogger wrappedLogger = new(logger);
        ILogManager logManager = Substitute.For<ILogManager>();
        logManager.GetClassLogger<CompositeNodeSource>().Returns(wrappedLogger);
        using CompositeNodeSource source = new([first, second], logManager);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        List<Node> nodes = await source.DiscoverNodes(timeout.Token).ToListAsync();

        Assert.That(nodes, Is.EqualTo(new[] { node }));
        logger.Received(fail ? 1 : 0).Error(Arg.Any<string>(), Arg.Any<FormatException>());
    }

    [Test]
    public async Task DiscoverNodes_Completes_without_sources()
    {
        using CompositeNodeSource source = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Assert.That(await source.DiscoverNodes(timeout.Token).ToListAsync(), Is.Empty);
    }

    private static async IAsyncEnumerable<Node> Enumerate(bool fail, Node? node)
    {
        await Task.Yield();
        if (fail) throw new FormatException("Invalid node record");
        if (node is not null) yield return node;
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
}
