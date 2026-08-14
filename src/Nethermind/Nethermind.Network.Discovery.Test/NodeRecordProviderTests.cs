// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using NSubstitute;
using NUnit.Framework;
using EnrForkId = Nethermind.Network.Enr.ForkId;
using NetworkForkId = Nethermind.Network.ForkId;

namespace Nethermind.Network.Discovery.Test;

public class NodeRecordProviderTests
{
    [TestCaseSource(nameof(ForkIdPublicationCases))]
    public async Task GetCurrentAsync_PublishesEthEntryFromEffectiveHeadForkId(
        NetworkForkId networkForkId,
        byte[] expectedForkHash,
        ulong expectedNext)
    {
        Block head = Build.A.Block.WithNumber(1).WithTimestamp(10).TestObject;
        NodeRecordProvider provider = CreateProvider(
            head,
            networkForkId,
            IPAddress.Parse("192.0.2.1"));

        NodeRecord record = await provider.GetCurrentAsync();
        EnrForkId? forkId = record.GetValue<EnrForkId>(EnrContentKey.Eth);

        Assert.That(forkId, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkId.Value.ForkHash, Is.EqualTo(expectedForkHash));
            Assert.That(forkId.Value.Next, Is.EqualTo(expectedNext));
            Assert.That(record.EnrSequence, Is.EqualTo(1_000));
        }
    }

    [Test]
    public async Task NewHeadBlock_RebuildsRecordWithMonotonicSequenceWhenForkIdChangesInSameTick()
    {
        Block initialHead = Build.A.Block.WithNumber(1).WithTimestamp(10).TestObject;
        Block newHead = Build.A.Block.WithNumber(2).WithTimestamp(20).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(initialHead);

        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        forkInfo.GetForkId(1, 10).Returns(new NetworkForkId(0x01020304, 20));
        forkInfo.GetForkId(2, 20).Returns(new NetworkForkId(0x05060708, 0));

        NodeRecordProvider provider = CreateProvider(
            blockTree,
            forkInfo,
            IPAddress.Parse("192.0.2.1"),
            timestampMilliseconds: 1_000);

        NodeRecord initialRecord = await provider.GetCurrentAsync();
        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(newHead));

        NodeRecord updatedRecord = await provider.GetCurrentAsync();
        EnrForkId? forkId = updatedRecord.GetValue<EnrForkId>(EnrContentKey.Eth);

        Assert.That(forkId, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updatedRecord.EnrSequence, Is.EqualTo(initialRecord.EnrSequence + 1));
            Assert.That(forkId.Value.ForkHash, Is.EqualTo(new byte[] { 5, 6, 7, 8 }));
            Assert.That(forkId.Value.Next, Is.Zero);
        }
    }

    [Test]
    public async Task NewHeadBlock_KeepsRecordWhenAdvertisedStateDoesNotChange()
    {
        Block initialHead = Build.A.Block.WithNumber(1).WithTimestamp(10).TestObject;
        Block newHead = Build.A.Block.WithNumber(2).WithTimestamp(20).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(initialHead);

        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        NetworkForkId forkId = new(0x01020304, 20);
        forkInfo.GetForkId(1, 10).Returns(forkId);
        forkInfo.GetForkId(2, 20).Returns(forkId);

        NodeRecordProvider provider = CreateProvider(
            blockTree,
            forkInfo,
            IPAddress.Parse("192.0.2.1"),
            timestampMilliseconds: 1_000);

        NodeRecord initialRecord = await provider.GetCurrentAsync();
        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(newHead));

        NodeRecord currentRecord = await provider.GetCurrentAsync();

        Assert.That(currentRecord, Is.SameAs(initialRecord));
        Assert.That(currentRecord.EnrSequence, Is.EqualTo(initialRecord.EnrSequence));
    }

    [TestCase("192.0.2.1", "192.0.2.1", null)]
    [TestCase("::ffff:192.0.2.1", "192.0.2.1", null)]
    [TestCase("2001:db8::1", null, "2001:db8::1")]
    [TestCase("255.255.255.255", null, null)] // IPAddress.None: unresolved external IP
    public async Task GetCurrentAsync_PublishesEndpointEntriesMatchingExternalIpFamily(
        string externalIp, string? expectedIp, string? expectedIp6)
    {
        Block head = Build.A.Block.WithNumber(1).WithTimestamp(10).TestObject;
        NodeRecordProvider provider = CreateProvider(head, new NetworkForkId(0x01020304, 20), IPAddress.Parse(externalIp));

        NodeRecord record = await provider.GetCurrentAsync();
        NodeRecord decoded = NodeRecord.FromEnrString(record.ToString());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.GetObj<IPAddress>(EnrContentKey.Ip), Is.EqualTo(expectedIp is null ? null : IPAddress.Parse(expectedIp)));
            Assert.That(decoded.GetValue<int>(EnrContentKey.Tcp), Is.EqualTo(expectedIp is null ? null : (int?)30303));
            Assert.That(decoded.GetValue<int>(EnrContentKey.Udp), Is.EqualTo(expectedIp is null ? null : (int?)30303));
            Assert.That(decoded.GetObj<IPAddress>(EnrContentKey.Ip6), Is.EqualTo(expectedIp6 is null ? null : IPAddress.Parse(expectedIp6)));
            Assert.That(decoded.GetValue<int>(EnrContentKey.Tcp6), Is.EqualTo(expectedIp6 is null ? null : (int?)30303));
            Assert.That(decoded.GetValue<int>(EnrContentKey.Udp6), Is.EqualTo(expectedIp6 is null ? null : (int?)30303));
        }
    }

    private static NodeRecordProvider CreateProvider(Block head, NetworkForkId forkId, IPAddress externalIp)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(head);
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        forkInfo.GetForkId(head.Header.Number, head.Header.Timestamp).Returns(forkId);
        return CreateProvider(blockTree, forkInfo, externalIp, timestampMilliseconds: 1_000);
    }

    private static NodeRecordProvider CreateProvider(
        IBlockTree blockTree,
        IForkInfo forkInfo,
        IPAddress externalIp,
        long timestampMilliseconds)
    {
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(IPAddress.Loopback, externalIp)));

        INetworkConfig networkConfig = Substitute.For<INetworkConfig>();
        networkConfig.P2PPort.Returns(30303);
        networkConfig.DiscoveryPort.Returns(30303);

        DateTime utcNow = DateTimeOffset.FromUnixTimeMilliseconds(timestampMilliseconds).UtcDateTime;
        ITimestamper timestamper = Substitute.For<ITimestamper>();
        timestamper.UtcNow.Returns(utcNow);
        timestamper.UnixTime.Returns(new UnixTime(utcNow));

        return new NodeRecordProvider(
            new InsecureProtectedPrivateKey(TestItem.PrivateKeyA),
            ipResolver,
            new EthereumEcdsa(0),
            networkConfig,
            blockTree,
            forkInfo,
            timestamper,
            LimboLogs.Instance);
    }

    private static IEnumerable<TestCaseData> ForkIdPublicationCases()
    {
        yield return new TestCaseData(
                new NetworkForkId(0x01020304, 20),
                new byte[] { 1, 2, 3, 4 },
                20UL)
            .SetName("GetCurrentAsync_publishes_standard_eth_fork_id");
        yield return new TestCaseData(
                new NetworkForkId(0xaabbccdd, ulong.MaxValue),
                new byte[] { 0xaa, 0xbb, 0xcc, 0xdd },
                ulong.MaxValue)
            .SetName("GetCurrentAsync_publishes_unsigned_max_next_fork");
    }
}
