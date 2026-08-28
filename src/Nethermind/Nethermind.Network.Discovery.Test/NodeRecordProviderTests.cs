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

    [Test]
    public async Task NewHeadBlock_DoesNotRepeatUnchangedEndpointWarnings()
    {
        Block initialHead = Build.A.Block.WithNumber(1).WithTimestamp(10).TestObject;
        Block newHead = Build.A.Block.WithNumber(2).WithTimestamp(20).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(initialHead);
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        NetworkForkId forkId = new(0x01020304, 20);
        forkInfo.GetForkId(1, 10).Returns(forkId);
        forkInfo.GetForkId(2, 20).Returns(forkId);
        ILogManager logManager = CreateWarningLogManager(out InterfaceLogger underlyingLogger);
        NodeRecordProvider provider = CreateProvider(
            blockTree,
            forkInfo,
            new IIPResolver.NethermindIp(IPAddress.Loopback, IPAddress.Parse("2001:db8::1")),
            timestampMilliseconds: 1_000,
            logManager);

        await provider.GetCurrentAsync();
        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(newHead));
        await provider.GetCurrentAsync();

        underlyingLogger.Received(1).Warn(Arg.Is<string>(message => message.StartsWith("External IPv6 address")));
        underlyingLogger.Received(1).Warn(Arg.Is<string>(message => message.StartsWith("No external IP address")));
    }

    [Test]
    public async Task NewHeadBlock_DoesNotResignWhenOnlyEndpointIssuesChange()
    {
        Block initialHead = Build.A.Block.WithNumber(1).WithTimestamp(10).TestObject;
        Block newHead = Build.A.Block.WithNumber(2).WithTimestamp(20).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(initialHead);
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        NetworkForkId forkId = new(0x01020304, 20);
        forkInfo.GetForkId(1, 10).Returns(forkId);
        forkInfo.GetForkId(2, 20).Returns(forkId);
        IIPResolver.NethermindIp initialIp = new(IPAddress.Loopback, IPAddress.Parse("192.0.2.1"));
        IIPResolver.NethermindIp updatedIp = new(
            IPAddress.Loopback,
            IPAddress.Parse("192.0.2.1"),
            IPAddress.Parse("2001:db8::1"));
        int resolveCalls = 0;
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(_ =>
            new ValueTask<IIPResolver.NethermindIp>(resolveCalls++ == 0 ? initialIp : updatedIp));
        ILogManager logManager = CreateWarningLogManager(out InterfaceLogger underlyingLogger);
        NodeRecordProvider provider = CreateProvider(
            blockTree,
            forkInfo,
            ipResolver,
            timestampMilliseconds: 1_000,
            logManager);

        NodeRecord initialRecord = await provider.GetCurrentAsync();
        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(newHead));
        NodeRecord currentRecord = await provider.GetCurrentAsync();
        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(newHead));
        NodeRecord repeatedRecord = await provider.GetCurrentAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(currentRecord, Is.SameAs(initialRecord));
            Assert.That(repeatedRecord, Is.SameAs(initialRecord));
            Assert.That(currentRecord.EnrSequence, Is.EqualTo(initialRecord.EnrSequence));
        }
        underlyingLogger.Received(1).Warn(Arg.Is<string>(message => message.StartsWith("External IPv6 address")));
    }

    [Test]
    public async Task GetCurrentAsync_WarnsWhenIpv4IsNotAdvertised()
    {
        ILogManager logManager = CreateWarningLogManager(out InterfaceLogger underlyingLogger);
        NodeRecordProvider provider = CreateProvider(
            Build.A.Block.WithNumber(1).WithTimestamp(10).TestObject,
            new NetworkForkId(0x01020304, 20),
            new IIPResolver.NethermindIp(IPAddress.IPv6Loopback, IPAddress.Parse("192.0.2.1")),
            logManager: logManager);

        await provider.GetCurrentAsync();

        underlyingLogger.Received(1).Warn(Arg.Is<string>(message => message.StartsWith("External IPv4 address")));
        underlyingLogger.Received(1).Warn(Arg.Is<string>(message => message.StartsWith("No external IP address")));
    }

    [TestCase("192.0.2.1", "192.0.2.1", null)]
    [TestCase("::ffff:192.0.2.1", "192.0.2.1", null)]
    [TestCase("2001:db8::1", null, null)] // IPv6 external with an IPv4 listener: endpoint suppressed
    [TestCase("255.255.255.255", null, null)] // IPAddress.None: unresolved external IP
    public async Task GetCurrentAsync_PublishesEndpointEntriesMatchingExternalIpFamily(
        string externalIp, string? expectedIp, string? expectedIp6)
    {
        Block head = Build.A.Block.WithNumber(1).WithTimestamp(10).TestObject;
        NodeRecordProvider provider = CreateProvider(head, new NetworkForkId(0x01020304, 20), IPAddress.Parse(externalIp));

        NodeRecord record = await provider.GetCurrentAsync();
        NodeRecord decoded = NodeRecord.FromEnrString(record.ToString());

        AssertEndpointEntries(decoded, expectedIp, expectedIp6);
    }

    [Test]
    public async Task GetCurrentAsync_PublishesBothEndpointFamiliesWhenResolved()
    {
        Block head = Build.A.Block.WithNumber(1).WithTimestamp(10).TestObject;
        NodeRecordProvider provider = CreateProvider(
            head,
            new NetworkForkId(0x01020304, 20),
            new IIPResolver.NethermindIp(
                IPAddress.IPv6Any,
                IPAddress.Parse("192.0.2.1"),
                IPAddress.Parse("2001:db8::1")));

        NodeRecord record = await provider.GetCurrentAsync();
        NodeRecord decoded = NodeRecord.FromEnrString(record.ToString());

        AssertEndpointEntries(decoded, "192.0.2.1", "2001:db8::1");
    }

    [Test]
    public async Task GetCurrentAsync_PublishesIpv6EntriesWhenListeningOnIpv6()
    {
        Block head = Build.A.Block.WithNumber(1).WithTimestamp(10).TestObject;
        NodeRecordProvider provider = CreateProvider(
            head,
            new NetworkForkId(0x01020304, 20),
            new IIPResolver.NethermindIp(IPAddress.IPv6Any, IPAddress.Parse("2001:db8::1")));

        NodeRecord record = await provider.GetCurrentAsync();
        NodeRecord decoded = NodeRecord.FromEnrString(record.ToString());

        AssertEndpointEntries(decoded, null, "2001:db8::1");
    }

    [Test]
    public async Task GetCurrentAsync_DoesNotPublishIpv6WhenNotListeningOnIpv6()
    {
        Block head = Build.A.Block.WithNumber(1).WithTimestamp(10).TestObject;
        NodeRecordProvider provider = CreateProvider(
            head,
            new NetworkForkId(0x01020304, 20),
            new IIPResolver.NethermindIp(
                IPAddress.Loopback,
                IPAddress.Parse("192.0.2.1"),
                IPAddress.Parse("2001:db8::1")));

        NodeRecord record = await provider.GetCurrentAsync();
        NodeRecord decoded = NodeRecord.FromEnrString(record.ToString());

        AssertEndpointEntries(decoded, "192.0.2.1", null);
    }

    [Test]
    public async Task GetCurrentAsync_PublishesIpv4WhenListeningOnMappedIpv4()
    {
        Block head = Build.A.Block.WithNumber(1).WithTimestamp(10).TestObject;
        NodeRecordProvider provider = CreateProvider(
            head,
            new NetworkForkId(0x01020304, 20),
            new IIPResolver.NethermindIp(
                IPAddress.Parse("::ffff:0.0.0.0"),
                IPAddress.Parse("192.0.2.1"),
                IPAddress.Parse("2001:db8::1")));

        NodeRecord record = await provider.GetCurrentAsync();
        NodeRecord decoded = NodeRecord.FromEnrString(record.ToString());

        AssertEndpointEntries(decoded, "192.0.2.1", null);
    }

    [Test]
    public async Task GetCurrentAsync_DoesNotPublishIpv4WhenBoundToSpecificIpv6()
    {
        // A socket bound to a specific native IPv6 address cannot accept IPv4, so the IPv4 family must
        // not be advertised even when an IPv4 external address is configured.
        Block head = Build.A.Block.WithNumber(1).WithTimestamp(10).TestObject;
        NodeRecordProvider provider = CreateProvider(
            head,
            new NetworkForkId(0x01020304, 20),
            new IIPResolver.NethermindIp(
                IPAddress.Parse("2001:db8::5"),
                IPAddress.Parse("192.0.2.1"),
                IPAddress.Parse("2001:db8::5")));

        NodeRecord record = await provider.GetCurrentAsync();
        NodeRecord decoded = NodeRecord.FromEnrString(record.ToString());

        AssertEndpointEntries(decoded, null, "2001:db8::5");
    }

    private static NodeRecordProvider CreateProvider(Block head, NetworkForkId forkId, IPAddress externalIp)
        => CreateProvider(head, forkId, new IIPResolver.NethermindIp(IPAddress.Loopback, externalIp));

    private static NodeRecordProvider CreateProvider(
        Block head,
        NetworkForkId forkId,
        IIPResolver.NethermindIp resolvedIp,
        ILogManager? logManager = null)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(head);
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        forkInfo.GetForkId(head.Header.Number, head.Header.Timestamp).Returns(forkId);
        return CreateProvider(blockTree, forkInfo, resolvedIp, timestampMilliseconds: 1_000, logManager: logManager);
    }

    private static NodeRecordProvider CreateProvider(
        IBlockTree blockTree,
        IForkInfo forkInfo,
        IPAddress externalIp,
        long timestampMilliseconds)
        => CreateProvider(
            blockTree,
            forkInfo,
            new IIPResolver.NethermindIp(IPAddress.Loopback, externalIp),
            timestampMilliseconds);

    private static NodeRecordProvider CreateProvider(
        IBlockTree blockTree,
        IForkInfo forkInfo,
        IIPResolver.NethermindIp resolvedIp,
        long timestampMilliseconds,
        ILogManager? logManager = null)
    {
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(resolvedIp));

        return CreateProvider(blockTree, forkInfo, ipResolver, timestampMilliseconds, logManager);
    }

    private static NodeRecordProvider CreateProvider(
        IBlockTree blockTree,
        IForkInfo forkInfo,
        IIPResolver ipResolver,
        long timestampMilliseconds,
        ILogManager? logManager = null)
    {

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
            logManager ?? LimboLogs.Instance);
    }

    private static void AssertEndpointEntries(NodeRecord decoded, string? expectedIp, string? expectedIp6)
    {
        int? expectedIpV6Port = expectedIp6 is null ? null : 30303;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.GetObj<IPAddress>(EnrContentKey.Ip), Is.EqualTo(expectedIp is null ? null : IPAddress.Parse(expectedIp)));
            Assert.That(decoded.GetValue<int>(EnrContentKey.Tcp), Is.EqualTo(expectedIp is null ? null : (int?)30303));
            Assert.That(decoded.GetValue<int>(EnrContentKey.Udp), Is.EqualTo(expectedIp is null ? null : (int?)30303));
            Assert.That(decoded.GetObj<IPAddress>(EnrContentKey.Ip6), Is.EqualTo(expectedIp6 is null ? null : IPAddress.Parse(expectedIp6)));
            Assert.That(decoded.GetValue<int>(EnrContentKey.Tcp6), Is.EqualTo(expectedIpV6Port));
            Assert.That(decoded.GetValue<int>(EnrContentKey.Udp6), Is.EqualTo(expectedIpV6Port));
        }
    }

    private static ILogManager CreateWarningLogManager(out InterfaceLogger underlyingLogger)
    {
        underlyingLogger = Substitute.For<InterfaceLogger>();
        underlyingLogger.IsWarn.Returns(true);
        ILogger logger = new(underlyingLogger);
        ILogManager logManager = Substitute.For<ILogManager>();
        logManager.GetClassLogger<NodeRecordProvider>().Returns(logger);
        return logManager;
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
