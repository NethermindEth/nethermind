// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Bootnode.Test;

[TestFixture]
public class BootnodeDiscoveryV5NodeSourceTests
{
    [Test]
    public async Task Discovery_only_enr_is_emitted_as_discovery_node()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        using PrivateKey currentPrivateKey = generator.Generate();
        (Node discoveryNode, NodeRecord nodeRecord) = await CreateDiscoveryNode(privateKey, "127.0.0.1", 30303);
        Node currentNode = new(currentPrivateKey.PublicKey, "127.0.0.2", 30303);
        BootnodeDiscoveryV5NodeSource nodeSource = new(
            new StaticKademlia([discoveryNode]),
            EmptyDiscovery.Instance,
            new DiscoveryConfig(),
            new KademliaConfig<Node> { CurrentNodeId = currentNode },
            LimboLogs.Instance);

        Node? emitted = null;
        await foreach (Node node in nodeSource.DiscoverNodes(CancellationToken.None))
        {
            emitted = node;
            break;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Node.TryFromEnr(nodeRecord, out _), Is.False);
            Assert.That(emitted, Is.Not.Null);
            Assert.That(emitted!.Id, Is.EqualTo(privateKey.PublicKey));
            Assert.That(emitted.Port, Is.Zero);
            Assert.That(emitted.DiscoveryPort, Is.EqualTo(30303));
            Assert.That(emitted.Enr, Is.EqualTo(nodeRecord));
        }
    }

    [Test]
    public async Task Node_added_during_initial_snapshot_is_emitted()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey initialKey = generator.Generate();
        using PrivateKey addedKey = generator.Generate();
        using PrivateKey currentKey = generator.Generate();
        (Node initialNode, _) = await CreateDiscoveryNode(initialKey, "127.0.0.1", 30303);
        (Node addedNode, _) = await CreateDiscoveryNode(addedKey, "127.0.0.2", 30304);
        Node currentNode = new(currentKey.PublicKey, "127.0.0.3", 30303);
        StaticKademlia kademlia = new([initialNode], addedNode);
        BootnodeDiscoveryV5NodeSource nodeSource = new(
            kademlia,
            EmptyDiscovery.Instance,
            new DiscoveryConfig(),
            new KademliaConfig<Node> { CurrentNodeId = currentNode },
            LimboLogs.Instance);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        List<Node> emittedNodes = [];

        await foreach (Node node in nodeSource.DiscoverNodes(timeout.Token))
        {
            emittedNodes.Add(node);
            if (emittedNodes.Count == 2)
            {
                break;
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(emittedNodes, Has.Count.EqualTo(2));
            Assert.That(emittedNodes, Has.Some.Property(nameof(Node.Id)).EqualTo(initialKey.PublicKey));
            Assert.That(emittedNodes, Has.Some.Property(nameof(Node.Id)).EqualTo(addedKey.PublicKey));
        }
    }

    private static async Task<(Node Node, NodeRecord NodeRecord)> CreateDiscoveryNode(
        PrivateKey privateKey,
        string host,
        int discoveryPort)
    {
        string dataDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        IProtectedPrivateKey protectedPrivateKey = new ProtectedPrivateKey(privateKey, dataDir);
        NetworkConfig networkConfig = new()
        {
            DiscoveryPort = discoveryPort,
            P2PPort = 0
        };
        BootnodeNodeRecordProvider provider = new(
            protectedPrivateKey,
            new EthereumEcdsa(1),
            networkConfig,
            LimboLogs.Instance,
            new IIPResolver.NethermindIp(System.Net.IPAddress.Loopback, System.Net.IPAddress.Loopback),
            dataDir);
        NodeRecord nodeRecord = await provider.GetCurrentAsync();
        Node discoveryNode = new(privateKey.PublicKey, host, discoveryPort)
        {
            Enr = nodeRecord,
            IsBootnode = true
        };

        return (discoveryNode, nodeRecord);
    }

    private sealed class StaticKademlia(IReadOnlyList<Node> nodes, Node? nodeAddedAfterIteration = null) : IKademlia<PublicKey, Node>
    {
        public event EventHandler<Node> OnNodeAdded = delegate { };

        public event EventHandler<Node> OnNodeRemoved = delegate { };

        public void AddOrRefresh(Node node) => throw new NotSupportedException();

        public void Remove(Node node) => throw new NotSupportedException();

        public Task Run(CancellationToken token) => throw new NotSupportedException();

        public Task Bootstrap(CancellationToken token) => throw new NotSupportedException();

        public Task<Node[]> LookupNodesClosest(PublicKey key, CancellationToken token, int? k = null) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<Node> LookupNodes(PublicKey key, CancellationToken token, int? maxResults = null) =>
            throw new NotSupportedException();

        public Node[] GetKNeighbour(PublicKey target, Node? excluding = null, bool excludeSelf = false) =>
            throw new NotSupportedException();

        public Node[] GetAllAtDistance(int distance) => throw new NotSupportedException();

        public IEnumerable<Node> IterateNodes()
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                yield return nodes[i];
            }

            if (nodeAddedAfterIteration is not null)
            {
                OnNodeAdded(this, nodeAddedAfterIteration);
            }
        }
    }

    private sealed class EmptyDiscovery : IKademliaDiscovery<PublicKey, Node>
    {
        public static EmptyDiscovery Instance { get; } = new();

        public async IAsyncEnumerable<Node> DiscoverNodes(
            int concurrentDiscoveryJobs,
            int lookupResultLimit,
            [EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
