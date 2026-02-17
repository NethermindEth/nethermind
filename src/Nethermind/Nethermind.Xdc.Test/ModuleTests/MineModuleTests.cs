// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test.ModuleTests;

[Parallelizable(ParallelScope.All)]
internal class MineModuleTests
{
    public async Task<(XdcTestBlockchain, XdcBlockTree)> Setup()
    {
        var blockchain = await XdcTestBlockchain.Create(useHotStuffModule: true);
        var tree = (XdcBlockTree)blockchain.BlockTree;

        return (blockchain, tree);
    }

    [Test]
    public async Task TestUpdateMultipleMasterNodes()
    {
        var (blockchain, tree) = await Setup();

        // this test is basically an emulation because our block producer test setup does not support saving snapshots yet
        // add blocks until the next gap block
        var spec = blockchain.SpecProvider.GetXdcSpec((XdcBlockHeader)tree.Head!.Header!);

        var oldHead = (XdcBlockHeader)tree.Head!.Header!;
        var snapshotBefore = blockchain.SnapshotManager.GetSnapshotByBlockNumber(oldHead.Number, blockchain.SpecProvider.GetXdcSpec((XdcBlockHeader)tree.Head!.Header!));

        Assert.That(snapshotBefore, Is.Not.Null);
        Assert.That(snapshotBefore.NextEpochCandidates.Length, Is.EqualTo(30));

        // simulate adding a new validator
        var newValidator = new PrivateKeyGenerator().Generate();
        blockchain.MasterNodeCandidates.Add(newValidator);

        // mine the gap block that should trigger master node update
        var gapBlock = await blockchain.AddBlock();
        while (!ISnapshotManager.IsTimeForSnapshot(tree.Head!.Header!.Number, spec))
        {
            gapBlock = await blockchain.AddBlock();
        }

        var newHead = (XdcBlockHeader)tree.Head!.Header!;
        Assert.That(newHead.Number, Is.EqualTo(gapBlock.Number));

        var snapshotAfter = blockchain.SnapshotManager.GetSnapshotByGapNumber(newHead.Number);

        Assert.That(snapshotAfter, Is.Not.Null);
        Assert.That(snapshotAfter.BlockNumber, Is.EqualTo(gapBlock.Number));
        Assert.That(snapshotAfter.NextEpochCandidates.Length, Is.EqualTo(blockchain.MasterNodeCandidates.Count));
        Assert.That(snapshotAfter.NextEpochCandidates.Contains(newValidator.Address), Is.True);
    }

    [Test]
    public async Task TestShouldMineOncePerRound()
    {
        var (xdcBlockchain, tree) = await Setup();

        var _hotstuff = (XdcHotStuff)xdcBlockchain.BlockProducerRunner;

        var blocksProposed = 0;
        xdcBlockchain.ConsensusModule.BlockProduced += (sender, args) =>
        {
            blocksProposed++;
        };

        var newRoundWaitHandle = new TaskCompletionSource();
        xdcBlockchain.XdcContext.NewRoundSetEvent += (sender, args) =>
        {
            newRoundWaitHandle.TrySetResult();
        };

        xdcBlockchain.StartHotStuffModule();

        var parentBlock = tree.Head!.Header!;
        var masterNodesAddresses = xdcBlockchain.MasterNodeCandidates.Select(pv => pv.Address).ToArray();

        var spec = xdcBlockchain.SpecProvider.GetXdcSpec((XdcBlockHeader)tree.Head!.Header!);

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
        var (blockchain, tree) = await Setup();

        blockchain.ChangeReleaseSpec((spec) =>
        {
            spec.EpochLength = 90;
            spec.Gap = 45;
        });

        IXdcReleaseSpec? spec = blockchain.SpecProvider.GetXdcSpec((XdcBlockHeader)tree.Head!.Header);

        var header = (XdcBlockHeader)blockchain.BlockTree.Head!.Header!;
        spec = blockchain.SpecProvider.GetXdcSpec(header);
        var snapshot = blockchain.SnapshotManager.GetSnapshotByBlockNumber(header.Number, spec);

        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot.BlockNumber, Is.EqualTo(0));
        Assert.That(snapshot.NextEpochCandidates.Length, Is.EqualTo(30));

        var gapBlock = await blockchain.AddBlock();
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

        var epochSwitchBlock = await blockchain.AddBlock();
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
