// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NUnit.Framework;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Network.Discovery.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class DiscoveryV5AppTests
{
    private MemDb _discoveryDb = null!;
    private MemDb _legacyDiscoveryDb = null!;
    private DiscoveryV5App _discoveryV5App = null!;
    private readonly List<IContainer> _containers = [];

    [OneTimeSetUp]
    public void OneTimeSetup() => Rlp.RegisterDecoder(typeof(NetworkNode), new NetworkNodeDecoder());

    [SetUp]
    public void Setup()
    {
        _discoveryDb = new MemDb();
        _legacyDiscoveryDb = new MemDb();
        _discoveryV5App = CreateDiscoveryV5App(IPAddress.Parse("8.8.8.8"));
    }

    private DiscoveryV5App CreateDiscoveryV5App(IPAddress externalIp)
    {
        NetworkConfig networkConfig = new()
        {
            Bootnodes = [],
            ExternalIp = externalIp.ToString()
        };
        IProtectedPrivateKey nodeKey = new InsecureProtectedPrivateKey(TestItem.PrivateKeyF);
        IIPResolver ipResolver = new FixedIpResolver(networkConfig);
        EthereumEcdsa ecdsa = new(0);
        ContainerBuilder builder = new();
        builder.RegisterInstance(LimboLogs.Instance).As<ILogManager>();
        builder.RegisterInstance(networkConfig).As<INetworkConfig>();
        builder.RegisterInstance(ipResolver).As<IIPResolver>();
        builder.RegisterInstance(nodeKey).Keyed<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey);
        builder.RegisterInstance(ecdsa).As<IEthereumEcdsa>().As<IEcdsa>();
        builder.RegisterInstance(new CryptoRandom()).As<ICryptoRandom>();
        builder.RegisterType<NodeRecordProvider>().As<INodeRecordProvider>().WithAttributeFiltering().SingleInstance();
        IContainer container = builder.Build();
        _containers.Add(container);

        return new DiscoveryV5App(
            container,
            nodeKey,
            ipResolver,
            networkConfig,
            new DiscoveryConfig { },
            _discoveryDb,
            _legacyDiscoveryDb,
            new ProcessExitSource(CancellationToken.None),
            LimboLogs.Instance
        );
    }

    [TearDown]
    public async Task Teardown()
    {
        if (_discoveryV5App is not null)
        {
            await _discoveryV5App.DisposeAsync();
        }
        for (int i = 0; i < _containers.Count; i++)
        {
            _containers[i].Dispose();
        }
        _containers.Clear();
        _discoveryDb.Dispose();
        _legacyDiscoveryDb.Dispose();
    }

    private static NodeRecord CreateTestEnr(Nethermind.Crypto.PrivateKey privateKey, IPAddress? ipAddress = null, int port = 30303, int? udpPort = null, bool includeTcp = true)
    {
        NodeRecord enr = new();
        enr.SetEntry(IdEntry.Instance);
        enr.SetEntry(new IpEntry(ipAddress ?? IPAddress.Loopback));
        enr.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
        if (includeTcp)
        {
            enr.SetEntry(new TcpEntry(port));
        }
        enr.SetEntry(new UdpEntry(udpPort ?? port));
        enr.EnrSequence = 1;
        new NodeRecordSigner(new EthereumEcdsa(0), privateKey).Sign(enr);

        return enr;
    }

    [Test]
    public void Should_Migrate_Correctly()
    {
        PrivateKey testPrivateKey1 = TestItem.PrivateKeyA;
        NodeRecord enr1 = CreateTestEnr(testPrivateKey1);
        _legacyDiscoveryDb[testPrivateKey1.PublicKey.Hash.Bytes] = enr1.ToRlpBytes();

        PrivateKey testPrivateKey2 = TestItem.PrivateKeyB;
        NodeRecord enr2 = CreateTestEnr(testPrivateKey2);
        _legacyDiscoveryDb[testPrivateKey2.PublicKey.Hash.Bytes] = enr2.ToRlpBytes();

        List<NodeRecord> loadedEnrs = _discoveryV5App.LoadStoredEnrs();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loadedEnrs.Count, Is.EqualTo(2), "Should get all records");
            Assert.That(_legacyDiscoveryDb.Count, Is.EqualTo(0), "Legacy DB should be empty");
            Assert.That(_discoveryDb.Count, Is.EqualTo(2), "DB should contain all items migrated");
        }
    }

    [Test]
    public void Should_Stop_Migration_From_V4_DB()
    {
        NetworkNode enode1 = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 1, 1);
        _legacyDiscoveryDb[enode1.NodeId.Bytes] = Rlp.Encode(enode1).Bytes;

        NetworkNode enode2 = new(TestItem.PublicKeyB, IPAddress.Loopback.ToString(), 1, 1);
        _legacyDiscoveryDb[enode2.NodeId.Bytes] = Rlp.Encode(enode2).Bytes;

        List<NodeRecord> loadedEnrs = _discoveryV5App.LoadStoredEnrs();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loadedEnrs.Count, Is.EqualTo(0), "Should not load any nodes if legacy DB contains enodes");
            Assert.That(_legacyDiscoveryDb.Count, Is.EqualTo(2), "Legacy DB should not be changed");
            Assert.That(_discoveryDb.Count, Is.EqualTo(0), "DB should not load any records");
        }
    }

    [Test]
    public void Should_Skip_Malformed_Legacy_Records_And_Migrate_Valid_Ones()
    {
        NetworkNode enode = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 1, 1);
        _legacyDiscoveryDb[enode.NodeId.Bytes] = Rlp.Encode(enode).Bytes;

        PrivateKey validPrivateKey = TestItem.PrivateKeyB;
        NodeRecord validEnr = CreateTestEnr(validPrivateKey);
        _legacyDiscoveryDb[validPrivateKey.PublicKey.Hash.Bytes] = validEnr.ToRlpBytes();

        List<NodeRecord> loadedEnrs = _discoveryV5App.LoadStoredEnrs();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loadedEnrs, Has.Count.EqualTo(1));
            Assert.That(loadedEnrs[0].EnrString, Is.EqualTo(validEnr.EnrString));
            Assert.That(_legacyDiscoveryDb, Has.Count.EqualTo(1), "Malformed legacy records should remain untouched");
            Assert.That(_discoveryDb, Has.Count.EqualTo(1), "Valid records should still be migrated");
        }
    }

    [Test]
    public void Should_Reject_Private_Ip_Enr()
    {
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Loopback);

        bool result = _discoveryV5App.TryGetNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.False);
        Assert.That(node, Is.Null);
    }

    [Test]
    public void Should_Accept_Private_Ip_Enr_On_Private_Deployment()
    {
        DiscoveryV5App privateDiscoveryApp = CreateDiscoveryV5App(IPAddress.Loopback);
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Loopback);

        bool result = privateDiscoveryApp.TryGetNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Host, Is.EqualTo(IPAddress.Loopback.ToString()));
    }

    [Test]
    public void Should_Accept_Public_Ip_Enr()
    {
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"));

        bool result = _discoveryV5App.TryGetNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Host, Is.EqualTo("8.8.8.8"));
    }

    [Test]
    public void Should_Use_Udp_Port_From_Enr()
    {
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), port: 30303, udpPort: 30304);

        bool result = _discoveryV5App.TryGetNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Port, Is.EqualTo(30304));
    }

    [Test]
    public void Should_Use_Udp_Port_From_Configured_Enr_Bootnode()
    {
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), udpPort: 9001, includeTcp: false);
        NetworkConfig networkConfig = new()
        {
            Bootnodes = [new NetworkNode(enr.EnrString)]
        };
        DiscoveryConfig discoveryConfig = new()
        {
            UseDefaultDiscv5Bootnodes = false
        };

        List<Node> bootNodes = _discoveryV5App.CreateBootNodes(networkConfig, discoveryConfig);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bootNodes, Has.Count.EqualTo(1));
            Assert.That(bootNodes[0].Port, Is.EqualTo(9001));
            Assert.That(bootNodes[0].Enr, Is.EqualTo(enr.EnrString));
        }
    }

    [Test]
    public void TryEnqueueNewEnr_Should_Deduplicate()
    {
        Queue<NodeRecord> queue = new();
        HashSet<NodeRecord> seenNodes = [];
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"));

        Assert.That(DiscoveryV5App.TryEnqueueNewEnr(queue, seenNodes, enr), Is.True);
        Assert.That(DiscoveryV5App.TryEnqueueNewEnr(queue, seenNodes, enr), Is.False);
        Assert.That(queue.Count, Is.EqualTo(1));
    }

    [Test]
    public void TryEnqueueNewEnr_Should_Respect_Tracked_Cap()
    {
        Queue<NodeRecord> queue = new();
        HashSet<NodeRecord> seenNodes = [];
        for (int i = 0; i < DiscoveryV5App.MaxTrackedEnrsPerWalk; i++)
        {
            seenNodes.Add(new NodeRecord());
        }

        NodeRecord candidate = CreateTestEnr(TestItem.PrivateKeyB, IPAddress.Parse("1.1.1.1"), port: 30304);

        Assert.That(DiscoveryV5App.TryEnqueueNewEnr(queue, seenNodes, candidate), Is.False);
        Assert.That(queue.Count, Is.EqualTo(0));
    }

    [Test]
    public void TryEnqueueNewEnr_Should_Respect_Pending_Cap()
    {
        Queue<NodeRecord> queue = new();
        NodeRecord existing = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"));
        for (int i = 0; i < DiscoveryV5App.MaxPendingEnrsPerWalk; i++)
        {
            queue.Enqueue(existing);
        }

        HashSet<NodeRecord> seenNodes = [];
        NodeRecord candidate = CreateTestEnr(TestItem.PrivateKeyB, IPAddress.Parse("1.1.1.1"), port: 30304);

        Assert.That(DiscoveryV5App.TryEnqueueNewEnr(queue, seenNodes, candidate), Is.False);
        Assert.That(queue.Count, Is.EqualTo(DiscoveryV5App.MaxPendingEnrsPerWalk));
    }
}
