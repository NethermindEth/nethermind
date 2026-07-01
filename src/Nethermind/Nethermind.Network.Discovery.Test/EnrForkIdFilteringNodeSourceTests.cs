// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class EnrForkIdFilteringNodeSourceTests
{
    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldSkipRejectedEnrNode(CancellationToken cancellationToken)
    {
        Node node = CreateNodeWithEnr();
        EnrForkIdFilteringNodeSource source = new(
            new TestNodeSource([node]),
            new TestEnrForkIdFilter(accept: false),
            LimboLogs.Instance);

        await using IAsyncEnumerator<Node> enumerator = source.DiscoverNodes(cancellationToken).GetAsyncEnumerator(cancellationToken);

        Assert.That(await enumerator.MoveNextAsync().AsTask(), Is.False);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldKeepNodesWithoutEnr(CancellationToken cancellationToken)
    {
        Node node = new(TestItem.PublicKeyA, "8.8.8.8", 30303);
        EnrForkIdFilteringNodeSource source = new(
            new TestNodeSource([node]),
            new TestEnrForkIdFilter(accept: false),
            LimboLogs.Instance);

        await using IAsyncEnumerator<Node> enumerator = source.DiscoverNodes(cancellationToken).GetAsyncEnumerator(cancellationToken);

        Assert.That(await enumerator.MoveNextAsync().AsTask(), Is.True);
        Assert.That(enumerator.Current, Is.EqualTo(node));
    }

    private static Node CreateNodeWithEnr()
    {
        NodeRecord record = new();
        record.SetEntry(IdEntry.Instance);
        record.SetEntry(new IpEntry(IPAddress.Parse("8.8.8.8")));
        record.SetEntry(new TcpEntry(30303));
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        record.EnrSequence = 1;
        new NodeRecordSigner(new EthereumEcdsa(0), TestItem.PrivateKeyA).Sign(record);

        return new Node(TestItem.PublicKeyA, "8.8.8.8", 30303)
        {
            Enr = record
        };
    }

    private sealed class TestNodeSource(IReadOnlyList<Node> nodes) : INodeSource
    {
        public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            for (int i = 0; i < nodes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return nodes[i];
            }
        }

        public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
    }
}
