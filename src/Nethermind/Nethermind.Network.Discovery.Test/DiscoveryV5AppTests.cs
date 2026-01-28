// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity.V4;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Serialization.Rlp;
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
    public void OneTimeSetup()
    {
        Rlp.RegisterDecoder(typeof(NetworkNode), new NetworkNodeDecoder());
    }

    [SetUp]
    public void Setup()
    {
        _discoveryDb = new MemDb();
        _legacyDiscoveryDb = new MemDb();
        _identityVerifier = new IdentityVerifierV4();
        NetworkConfig networkConfig = new()
        {
            Bootnodes = [],
            ExternalIp = IPAddress.Loopback.ToString()
        };
        _discoveryV5App = new DiscoveryV5App(
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

    private ENR CreateTestEnrBytes(Nethermind.Crypto.PrivateKey privateKey)
    {
        IdentitySignerV4 signer = new(privateKey.KeyBytes);

        ENR enr = new EnrBuilder()
            .WithIdentityScheme(_identityVerifier, signer)
            .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
            .WithEntry(EnrEntryKey.Ip, new EntryIp(IPAddress.Loopback))
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(signer.PublicKey))
            .WithEntry(EnrEntryKey.Tcp, new EntryTcp(30303))
            .WithEntry(EnrEntryKey.Udp, new EntryUdp(30303))
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
}
