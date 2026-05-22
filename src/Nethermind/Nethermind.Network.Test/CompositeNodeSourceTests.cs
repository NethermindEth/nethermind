// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test;

public class CompositeNodeSourceTests
{
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
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldStopInnerSources_WhenEnumerationIsDisposed(CancellationToken token)
    {
        TestNodeSource innerSource = new();
        CompositeNodeSource compositeNodeSource = new(innerSource);
        Node node = new(TestItem.PublicKeyA, "1.2.3.4", 1234);
        innerSource.AddNode(node);

        List<Node> nodes = await compositeNodeSource.DiscoverNodes(CancellationToken.None).Take(1).ToListAsync(token);

        Assert.That(nodes, Is.EqualTo(new[] { node }));
    }
}
