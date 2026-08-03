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
using Nethermind.Network.Discovery.Discv5.Kademlia;
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
        IProtectedPrivateKey protectedPrivateKey = new ProtectedPrivateKey(privateKey, TestContext.CurrentContext.WorkDirectory);
        NetworkConfig networkConfig = new()
        {
            DiscoveryPort = 30303,
            P2PPort = 0
        };
        BootnodeNodeRecordProvider provider = new(
            protectedPrivateKey,
            new StaticIpResolver(System.Net.IPAddress.Loopback),
            new EthereumEcdsa(1),
            networkConfig,
            LimboLogs.Instance,
            new BootnodeExternalIps(System.Net.IPAddress.Loopback, System.Net.IPAddress.Loopback, null),
            TestContext.CurrentContext.WorkDirectory);
        NodeRecord nodeRecord = await provider.GetCurrentAsync();
        Node discoveryNode = new(privateKey.PublicKey, "127.0.0.1", 30303)
        {
            Enr = nodeRecord,
            IsBootnode = true
        };
        Node currentNode = new(currentPrivateKey.PublicKey, "127.0.0.2", 30303);
        BootnodeDiscoveryV5NodeSource nodeSource = new(
            new StaticKademlia([discoveryNode]),
            EmptyDiscovery.Instance,
            new DiscoveryConfig(),
            new KademliaConfig<Node> { CurrentNodeId = currentNode },
            ExecutionLayerDiscv5RecordFilter.Instance,
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

    private sealed class StaticIpResolver(System.Net.IPAddress address) : IIPResolver
    {
        public ValueTask<IIPResolver.NethermindIp> Resolve(CancellationToken cancellationToken = default) =>
            new(new IIPResolver.NethermindIp(address, address));
    }

    private sealed class StaticKademlia(IReadOnlyList<Node> nodes) : IKademlia<PublicKey, Node>
    {
        public event EventHandler<Node> OnNodeAdded
        {
            add { }
            remove { }
        }

        public event EventHandler<Node> OnNodeRemoved
        {
            add { }
            remove { }
        }

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

        public IEnumerable<Node> IterateNodes() => nodes;
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
