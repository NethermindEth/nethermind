// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity.V4;
using NSubstitute;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NUnit.Framework;
using System.Collections.Generic;
using System.Net;
using ENR = Lantern.Discv5.Enr.Enr;

namespace Nethermind.Network.Discovery.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class DiscoveryV5AppTests
{
    private MemDb _discoveryDb = null!;
    private MemDb _legacyDiscoveryDb = null!;
    private IdentityVerifierV4 _identityVerifier = null!;
    private DiscoveryV5App _discoveryV5App = null!;

    [OneTimeSetUp]
    public void OneTimeSetup() => Rlp.RegisterDecoder(typeof(NetworkNode), new NetworkNodeDecoder());

    [SetUp]
    public void Setup()
    {
        _discoveryDb = new MemDb();
        _legacyDiscoveryDb = new MemDb();
        _identityVerifier = new IdentityVerifierV4();
        _discoveryV5App = CreateDiscoveryV5App(IPAddress.Parse("8.8.8.8"));
    }

    private DiscoveryV5App CreateDiscoveryV5App(IPAddress externalIp)
    {
        NetworkConfig networkConfig = new()
        {
            Bootnodes = [],
            ExternalIp = externalIp.ToString()
        };
        return new DiscoveryV5App(
            new InsecureProtectedPrivateKey(TestItem.PrivateKeyF),
            new FixedIpResolver(networkConfig),
            networkConfig,
            new DiscoveryConfig { },
            _discoveryDb,
            _legacyDiscoveryDb,
            LimboLogs.Instance
        );
    }

    [TearDown]
    public void Teardown()
    {
        _discoveryDb.Dispose();
        _legacyDiscoveryDb.Dispose();
    }

    private ENR CreateTestEnrBytes(Nethermind.Crypto.PrivateKey privateKey, IPAddress? ipAddress = null, int port = 30303)
    {
        IdentitySignerV4 signer = new(privateKey.KeyBytes);

        ENR enr = new EnrBuilder()
            .WithIdentityScheme(_identityVerifier, signer)
            .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
            .WithEntry(EnrEntryKey.Ip, new EntryIp(ipAddress ?? IPAddress.Loopback))
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(signer.PublicKey))
            .WithEntry(EnrEntryKey.Tcp, new EntryTcp(port))
            .WithEntry(EnrEntryKey.Udp, new EntryUdp(port))
            .Build();

        return enr;
    }

    [Test]
    public void Should_Migrate_Correctly()
    {
        PrivateKey testPrivateKey1 = TestItem.PrivateKeyA;
        ENR enr1 = CreateTestEnrBytes(testPrivateKey1);
        _legacyDiscoveryDb[enr1.NodeId] = enr1.EncodeRecord();

        PrivateKey testPrivateKey2 = TestItem.PrivateKeyB;
        ENR enr2 = CreateTestEnrBytes(testPrivateKey2);
        _legacyDiscoveryDb[enr2.NodeId] = enr2.EncodeRecord();

        List<ENR> loadedEnrs = _discoveryV5App.LoadStoredEnrs();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loadedEnrs, Has.Count.EqualTo(2), "Should get all records");
            Assert.That(_legacyDiscoveryDb, Has.Count.EqualTo(0), "Legacy DB should be empty");
            Assert.That(_discoveryDb, Has.Count.EqualTo(2), "DB should contain all items migrated");
        }
    }

    [Test]
    public void Should_Stop_Migration_From_V4_DB()
    {
        NetworkNode enode1 = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 1, 1);
        _legacyDiscoveryDb[enode1.NodeId.Bytes] = Rlp.Encode(enode1).Bytes;

        NetworkNode enode2 = new(TestItem.PublicKeyB, IPAddress.Loopback.ToString(), 1, 1);
        _legacyDiscoveryDb[enode2.NodeId.Bytes] = Rlp.Encode(enode2).Bytes;

        List<ENR> loadedEnrs = _discoveryV5App.LoadStoredEnrs();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loadedEnrs, Has.Count.EqualTo(0), "Should not load any nodes if legacy DB contains enodes");
            Assert.That(_legacyDiscoveryDb, Has.Count.EqualTo(2), "Legacy DB should not be changed");
            Assert.That(_discoveryDb, Has.Count.EqualTo(0), "DB should not load any records");
        }
    }

    [Test]
    public void Should_Reject_Private_Ip_Enr()
    {
        ENR enr = CreateTestEnrBytes(TestItem.PrivateKeyA, IPAddress.Loopback);

        bool result = _discoveryV5App.TryGetNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.False);
        Assert.That(node, Is.Null);
    }

    [Test]
    public void Should_Accept_Private_Ip_Enr_On_Private_Deployment()
    {
        DiscoveryV5App privateDiscoveryApp = CreateDiscoveryV5App(IPAddress.Loopback);
        ENR enr = CreateTestEnrBytes(TestItem.PrivateKeyA, IPAddress.Loopback);

        bool result = privateDiscoveryApp.TryGetNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Host, Is.EqualTo(IPAddress.Loopback.ToString()));
    }

    [Test]
    public void Should_Accept_Public_Ip_Enr()
    {
        ENR enr = CreateTestEnrBytes(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"));

        bool result = _discoveryV5App.TryGetNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Host, Is.EqualTo("8.8.8.8"));
    }

    [Test]
    public void TryEnqueueNewEnr_Should_Deduplicate()
    {
        Queue<IEnr> queue = new();
        HashSet<IEnr> seenNodes = [];
        ENR enr = CreateTestEnrBytes(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"));

        Assert.That(DiscoveryV5App.TryEnqueueNewEnr(queue, seenNodes, enr), Is.True);
        Assert.That(DiscoveryV5App.TryEnqueueNewEnr(queue, seenNodes, enr), Is.False);
        Assert.That(queue.Count, Is.EqualTo(1));
    }

    [Test]
    public void TryEnqueueNewEnr_Should_Respect_Tracked_Cap()
    {
        Queue<IEnr> queue = new();
        HashSet<IEnr> seenNodes = [];
        for (int i = 0; i < DiscoveryV5App.MaxTrackedEnrsPerWalk; i++)
        {
            seenNodes.Add(Substitute.For<IEnr>());
        }

        ENR candidate = CreateTestEnrBytes(TestItem.PrivateKeyB, IPAddress.Parse("1.1.1.1"), port: 30304);

        Assert.That(DiscoveryV5App.TryEnqueueNewEnr(queue, seenNodes, candidate), Is.False);
        Assert.That(queue.Count, Is.EqualTo(0));
    }

    [Test]
    public void TryEnqueueNewEnr_Should_Respect_Pending_Cap()
    {
        Queue<IEnr> queue = new();
        ENR existing = CreateTestEnrBytes(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"));
        for (int i = 0; i < DiscoveryV5App.MaxPendingEnrsPerWalk; i++)
        {
            queue.Enqueue(existing);
        }

        HashSet<IEnr> seenNodes = [];
        ENR candidate = CreateTestEnrBytes(TestItem.PrivateKeyB, IPAddress.Parse("1.1.1.1"), port: 30304);

        Assert.That(DiscoveryV5App.TryEnqueueNewEnr(queue, seenNodes, candidate), Is.False);
        Assert.That(queue.Count, Is.EqualTo(DiscoveryV5App.MaxPendingEnrsPerWalk));
    }
}
