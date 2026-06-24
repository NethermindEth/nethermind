// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    [Test]
    public async Task GetCurrentAsync_PublishesEthEntryFromEffectiveHeadForkId()
    {
        Block head = Build.A.Block.WithNumber(1).WithTimestamp(10).TestObject;
        NodeRecordProvider provider = CreateProvider(
            head,
            new NetworkForkId(0x01020304, 20),
            IPAddress.Parse("192.0.2.1"));

        NodeRecord record = await provider.GetCurrentAsync();
        EnrForkId? forkId = record.GetValue<EnrForkId>(EnrContentKey.Eth);

        Assert.That(forkId, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkId.Value.ForkHash, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            Assert.That(forkId.Value.Next, Is.EqualTo(20));
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
}
