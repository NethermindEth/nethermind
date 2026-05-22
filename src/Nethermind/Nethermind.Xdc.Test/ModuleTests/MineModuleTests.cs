// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test.ModuleTests;

[Parallelizable(ParallelScope.All)]
internal class MineModuleTests
{
    [Test]
    public async Task TestUpdateMultipleMasterNodes()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(useHotStuffModule: true);
        XdcBlockTree tree = (XdcBlockTree)blockchain.BlockTree;

        // this test is basically an emulation because our block producer test setup does not support saving snapshots yet
        // add blocks until the next gap block
        IXdcReleaseSpec spec = blockchain.SpecProvider.GetXdcSpec((XdcBlockHeader)tree.Head!.Header!);

        XdcBlockHeader oldHead = (XdcBlockHeader)tree.Head!.Header!;
        Snapshot? snapshotBefore = blockchain.SnapshotManager.GetSnapshotByBlockNumber(oldHead.Number, blockchain.SpecProvider.GetXdcSpec((XdcBlockHeader)tree.Head!.Header!));

        Assert.That(snapshotBefore, Is.Not.Null);
        Assert.That(snapshotBefore.NextEpochCandidates.Length, Is.EqualTo(30));

        // simulate adding a new validator
        PrivateKey newValidator = new PrivateKeyGenerator().Generate();
        blockchain.MasterNodeCandidates.Add(newValidator);

        // mine the gap block that should trigger master node update
        Block gapBlock = await blockchain.AddBlock();
        while (!ISnapshotManager.IsTimeForSnapshot(tree.Head!.Header!.Number, spec))
        {
            gapBlock = await blockchain.AddBlock();
        }

        XdcBlockHeader newHead = (XdcBlockHeader)tree.Head!.Header!;
        Assert.That(newHead.Number, Is.EqualTo(gapBlock.Number));

        Snapshot? snapshotAfter = blockchain.SnapshotManager.GetSnapshotByGapNumber(newHead.Number);

        Assert.That(snapshotAfter, Is.Not.Null);
        Assert.That(snapshotAfter.BlockNumber, Is.EqualTo(gapBlock.Number));
        Assert.That(snapshotAfter.NextEpochCandidates.Length, Is.EqualTo(blockchain.MasterNodeCandidates.Count));
        Assert.That(snapshotAfter.NextEpochCandidates.Contains(newValidator.Address), Is.True);
    }

    [Test]
    public async Task TestShouldMineOncePerRound()
    {
        using XdcTestBlockchain xdcBlockchain = await XdcTestBlockchain.Create(useHotStuffModule: true);
        XdcBlockTree tree = (XdcBlockTree)xdcBlockchain.BlockTree;

        XdcHotStuff _hotstuff = (XdcHotStuff)xdcBlockchain.BlockProducerRunner;

        int blocksProposed = 0;
        xdcBlockchain.ConsensusModule.BlockProduced += (sender, args) =>
        {
            blocksProposed++;
        };

        TaskCompletionSource newRoundWaitHandle = new();
        xdcBlockchain.XdcContext.NewRoundSetEvent += (sender, args) =>
        {
            newRoundWaitHandle.TrySetResult();
        };

        xdcBlockchain.StartHotStuffModule();

        BlockHeader parentBlock = tree.Head!.Header!;
        Address[] masterNodesAddresses = xdcBlockchain.MasterNodeCandidates.Select(pv => pv.Address).ToArray();

        IXdcReleaseSpec spec = xdcBlockchain.SpecProvider.GetXdcSpec((XdcBlockHeader)tree.Head!.Header!);

        await xdcBlockchain.TriggerAndSimulateBlockProposalAndVoting();

        Task firstTask = await Task.WhenAny(newRoundWaitHandle.Task, Task.Delay(5_000));
        if (firstTask != newRoundWaitHandle.Task)
        {
            Assert.Fail("Timeout waiting for new round event");
        }
        blocksProposed.Should().Be(1);
    }

    [Test]
    public async Task TestUpdateMasterNodes()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(useHotStuffModule: true);
        XdcBlockTree tree = (XdcBlockTree)blockchain.BlockTree;

        blockchain.ChangeReleaseSpec((spec) =>
        {
            spec.EpochLength = 90;
            spec.Gap = 45;
        });

        IXdcReleaseSpec? spec = blockchain.SpecProvider.GetXdcSpec((XdcBlockHeader)tree.Head!.Header);

        XdcBlockHeader header = (XdcBlockHeader)blockchain.BlockTree.Head!.Header!;
        spec = blockchain.SpecProvider.GetXdcSpec(header);
        Snapshot? snapshot = blockchain.SnapshotManager.GetSnapshotByBlockNumber(header.Number, spec);

        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot.BlockNumber, Is.EqualTo(0));
        Assert.That(snapshot.NextEpochCandidates.Length, Is.EqualTo(30));

        Block gapBlock = await blockchain.AddBlock();
        while (!ISnapshotManager.IsTimeForSnapshot(tree.Head!.Header!.Number, spec))
        {
            gapBlock = await blockchain.AddBlock();
        }

        Assert.That(gapBlock.Number, Is.EqualTo(spec.Gap));

        header = (XdcBlockHeader)gapBlock.Header!;
        spec = blockchain.SpecProvider.GetXdcSpec(header);
        snapshot = blockchain.SnapshotManager.GetSnapshotByGapNumber(header.Number);

        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot.BlockNumber, Is.EqualTo(gapBlock.Number));
        Assert.That(snapshot.NextEpochCandidates.Length, Is.EqualTo(blockchain.MasterNodeCandidates.Count));

        Block epochSwitchBlock = await blockchain.AddBlock();
        while (!blockchain.EpochSwitchManager.IsEpochSwitchAtBlock((XdcBlockHeader)tree.Head!.Header!))
        {
            epochSwitchBlock = await blockchain.AddBlock();
        }

        Assert.That(epochSwitchBlock.Number, Is.EqualTo(spec.EpochLength));

        header = (XdcBlockHeader)epochSwitchBlock.Header!;
        // --- Validate header fields
        int validatorCount = header.Validators!.Length / Address.Size;
        int penaltyCount = header.Penalties!.Length / Address.Size;

        Assert.That(validatorCount, Is.EqualTo(spec.MaxMasternodes));
    }
}
