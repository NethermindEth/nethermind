// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using System;
using System.Threading.Tasks;
using EnrForkId = Nethermind.Network.Enr.ForkId;
using NetworkForkId = Nethermind.Network.ForkId;

namespace Nethermind.Network.Discovery.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class NodeRecordProviderTests
{
    private static readonly DateTime EnrSequenceTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly ulong EnrSequence = (ulong)new DateTimeOffset(EnrSequenceTime).ToUnixTimeMilliseconds();

    [Test]
    public async Task Current_includes_eth_forkid_entry()
    {
        Block head = CreateBlock(64, 1_000);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(head);
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        NetworkForkId expectedForkId = new(0x01020304, 128);
        forkInfo.GetForkId(head.Number, head.Timestamp).Returns(expectedForkId);

        using NodeRecordProvider provider = CreateProvider(blockTree, forkInfo);

        NodeRecord current = await provider.GetCurrentAsync();

        AssertForkId(current, expectedForkId);
        Assert.That(current.EnrSequence, Is.EqualTo(EnrSequence));
        AssertForkId(NodeRecord.FromEnrString(current.EnrString), expectedForkId);
    }

    [Test]
    public async Task Current_includes_eth_forkid_when_next_exceeds_signed_long_range()
    {
        Block head = CreateBlock(64, 1_000);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(head);
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        NetworkForkId forkId = new(0x01020304, (ulong)long.MaxValue + 1);
        forkInfo.GetForkId(head.Number, head.Timestamp).Returns(forkId);

        using NodeRecordProvider provider = CreateProvider(blockTree, forkInfo);

        NodeRecord current = await provider.GetCurrentAsync();

        AssertForkId(current, forkId);
        Assert.That(current.EnrSequence, Is.EqualTo(EnrSequence));
        AssertForkId(NodeRecord.FromEnrString(current.EnrString), forkId);
    }

    [Test]
    public async Task Current_uses_sync_pivot_when_head_is_genesis()
    {
        Block genesis = CreateBlock(0, 1_000);
        Block pivot = CreateBlock(128, 2_000);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(genesis);
        blockTree.Genesis.Returns(genesis.Header);
        blockTree.SyncPivot.Returns((pivot.Number, pivot.Hash!));
        const BlockTreeLookupOptions lookupOptions =
            BlockTreeLookupOptions.TotalDifficultyNotNeeded |
            BlockTreeLookupOptions.DoNotCreateLevelIfMissing;
        blockTree.FindHeader(pivot.Hash!, lookupOptions, pivot.Number).Returns(pivot.Header);
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        NetworkForkId expectedForkId = new(0x01020304, 256);
        forkInfo.GetForkId(pivot.Number, pivot.Timestamp).Returns(expectedForkId);

        using NodeRecordProvider provider = CreateProvider(blockTree, forkInfo);

        AssertForkId(await provider.GetCurrentAsync(), expectedForkId);
        forkInfo.Received(1).GetForkId(pivot.Number, pivot.Timestamp);
    }

    [Test]
    public async Task Current_reuses_resolved_sync_pivot_header()
    {
        Block genesis = CreateBlock(0, 1_000);
        Block pivot = CreateBlock(128, 2_000);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(genesis);
        blockTree.Genesis.Returns(genesis.Header);
        blockTree.SyncPivot.Returns((pivot.Number, pivot.Hash!));
        const BlockTreeLookupOptions lookupOptions =
            BlockTreeLookupOptions.TotalDifficultyNotNeeded |
            BlockTreeLookupOptions.DoNotCreateLevelIfMissing;
        blockTree.FindHeader(pivot.Hash!, lookupOptions, pivot.Number).Returns(pivot.Header);
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        NetworkForkId expectedForkId = new(0x01020304, 256);
        forkInfo.GetForkId(pivot.Number, pivot.Timestamp).Returns(expectedForkId);
        using NodeRecordProvider provider = CreateProvider(blockTree, forkInfo);

        NodeRecord initial = await provider.GetCurrentAsync();
        NodeRecord current = await provider.GetCurrentAsync();

        Assert.That(current, Is.SameAs(initial));
        blockTree.Received(1).FindHeader(pivot.Hash!, lookupOptions, pivot.Number);
        forkInfo.Received(1).GetForkId(pivot.Number, pivot.Timestamp);
    }

    [Test]
    public async Task Current_refreshes_eth_forkid_when_sync_pivot_becomes_available()
    {
        Block genesis = CreateBlock(0, 1_000);
        Block pivot = CreateBlock(128, 2_000);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(genesis);
        blockTree.Genesis.Returns(genesis.Header);
        blockTree.SyncPivot.Returns((pivot.Number, pivot.Hash!));
        const BlockTreeLookupOptions lookupOptions =
            BlockTreeLookupOptions.TotalDifficultyNotNeeded |
            BlockTreeLookupOptions.DoNotCreateLevelIfMissing;
        blockTree.FindHeader(pivot.Hash!, lookupOptions, pivot.Number).Returns((BlockHeader?)null, pivot.Header);
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        NetworkForkId genesisForkId = new(0x01020304, 128);
        NetworkForkId pivotForkId = new(0x05060708, 256);
        forkInfo.GetForkId(genesis.Number, genesis.Timestamp).Returns(genesisForkId);
        forkInfo.GetForkId(pivot.Number, pivot.Timestamp).Returns(pivotForkId);
        using NodeRecordProvider provider = CreateProvider(blockTree, forkInfo);

        NodeRecord initial = await provider.GetCurrentAsync();
        NodeRecord updated = await provider.GetCurrentAsync();

        Assert.That(updated, Is.Not.SameAs(initial));
        Assert.That(updated.EnrSequence, Is.EqualTo(initial.EnrSequence + 1));
        AssertForkId(updated, pivotForkId);
    }

    [Test]
    public async Task NewHeadBlock_updates_eth_forkid_and_bumps_sequence()
    {
        Block initialHead = CreateBlock(64, 1_000);
        Block updatedHead = CreateBlock(128, 2_000);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(initialHead, updatedHead);
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        NetworkForkId initialForkId = new(0x01020304, 128);
        NetworkForkId updatedForkId = new(0x05060708, 0);
        forkInfo.GetForkId(initialHead.Number, initialHead.Timestamp).Returns(initialForkId);
        forkInfo.GetForkId(updatedHead.Number, updatedHead.Timestamp).Returns(updatedForkId);
        using NodeRecordProvider provider = CreateProvider(blockTree, forkInfo);
        NodeRecord current = await provider.GetCurrentAsync();
        ulong initialSequence = current.EnrSequence;

        blockTree.NewHeadBlock += Raise.EventWith(blockTree, new BlockEventArgs(updatedHead));

        NodeRecord updated = await provider.GetCurrentAsync();
        Assert.That(updated, Is.Not.SameAs(current));
        Assert.That(updated.EnrSequence, Is.EqualTo(initialSequence + 1));
        AssertForkId(updated, updatedForkId);
        AssertForkId(NodeRecord.FromEnrString(updated.EnrString), updatedForkId);
    }

    [Test]
    public async Task NewHeadBlock_does_not_bump_sequence_when_forkid_is_unchanged()
    {
        Block initialHead = CreateBlock(64, 1_000);
        Block updatedHead = CreateBlock(65, 2_000);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(initialHead, updatedHead);
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        NetworkForkId forkId = new(0x01020304, 128);
        forkInfo.GetForkId(initialHead.Number, initialHead.Timestamp).Returns(forkId);
        forkInfo.GetForkId(updatedHead.Number, updatedHead.Timestamp).Returns(forkId);
        using NodeRecordProvider provider = CreateProvider(blockTree, forkInfo);
        NodeRecord current = await provider.GetCurrentAsync();
        ulong initialSequence = current.EnrSequence;

        blockTree.NewHeadBlock += Raise.EventWith(blockTree, new BlockEventArgs(updatedHead));

        NodeRecord unchanged = await provider.GetCurrentAsync();
        Assert.That(unchanged, Is.SameAs(current));
        Assert.That(unchanged.EnrSequence, Is.EqualTo(initialSequence));
        AssertForkId(unchanged, forkId);
    }

    [Test]
    public async Task NewHeadBlock_adds_eth_forkid_when_current_record_has_no_header_context()
    {
        Block updatedHead = CreateBlock(64, 1_000);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        NetworkForkId forkId = new(0x01020304, 128);
        forkInfo.GetForkId(updatedHead.Number, updatedHead.Timestamp).Returns(forkId);
        using NodeRecordProvider provider = CreateProvider(blockTree, forkInfo);
        NodeRecord current = await provider.GetCurrentAsync();
        ulong initialSequence = current.EnrSequence;

        blockTree.NewHeadBlock += Raise.EventWith(blockTree, new BlockEventArgs(updatedHead));

        NodeRecord updated = await provider.GetCurrentAsync();
        Assert.That(updated, Is.Not.SameAs(current));
        Assert.That(updated.EnrSequence, Is.EqualTo(initialSequence + 1));
        AssertForkId(updated, forkId);
    }

    [Test]
    public async Task Dispose_unsubscribes_and_blocks_current_access()
    {
        Block initialHead = CreateBlock(64, 1_000);
        Block updatedHead = CreateBlock(128, 2_000);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(initialHead, updatedHead);
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        NetworkForkId forkId = new(0x01020304, 128);
        forkInfo.GetForkId(initialHead.Number, initialHead.Timestamp).Returns(forkId);
        using NodeRecordProvider provider = CreateProvider(blockTree, forkInfo);
        _ = await provider.GetCurrentAsync();

        provider.Dispose();
        forkInfo.ClearReceivedCalls();

        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            _ = await provider.GetCurrentAsync();
        });
        Assert.DoesNotThrow(() =>
        {
            blockTree.NewHeadBlock += Raise.EventWith(blockTree, new BlockEventArgs(updatedHead));
        });
        forkInfo.DidNotReceive().GetForkId(updatedHead.Number, updatedHead.Timestamp);
    }

    private static Block CreateBlock(long number, ulong timestamp) =>
        Build.A.Block
            .WithNumber(number)
            .WithTimestamp(timestamp)
            .TestObject;

    private static NodeRecordProvider CreateProvider(IBlockTree blockTree, IForkInfo forkInfo)
    {
        NetworkConfig networkConfig = new()
        {
            ExternalIp = "8.8.8.8",
            P2PPort = 30303,
            DiscoveryPort = 30303
        };
        IProtectedPrivateKey nodeKey = new InsecureProtectedPrivateKey(TestItem.PrivateKeyA);

        return new NodeRecordProvider(
            nodeKey,
            new FixedIpResolver(networkConfig),
            new EthereumEcdsa(0),
            networkConfig,
            blockTree,
            forkInfo,
            new ManualTimestamper(EnrSequenceTime),
            LimboLogs.Instance);
    }

    private static void AssertForkId(NodeRecord nodeRecord, NetworkForkId expected)
    {
        EnrForkId? forkId = nodeRecord.GetValue<EnrForkId>(EnrContentKey.Eth);

        Assert.That(forkId, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkId!.Value.ForkHash, Is.EqualTo(expected.HashBytes));
            Assert.That(forkId.Value.NextBlock, Is.EqualTo(expected.Next));
        }
    }
}
