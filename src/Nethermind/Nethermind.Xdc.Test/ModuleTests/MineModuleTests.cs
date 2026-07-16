// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading;
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

        TaskCompletionSource newRoundWaitHandle = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        Assert.That(blocksProposed, Is.EqualTo(1));
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

    [Test]
    public async Task TestStartProposesFirstBlockWhileSyncingAtGenesis()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 0, useHotStuffModule: true);
        XdcBlockTree tree = (XdcBlockTree)blockchain.BlockTree;

        XdcBlockHeader head = (XdcBlockHeader)tree.Head!.Header;
        Assert.That(tree.IsSyncing().isSyncing, Is.True);
        Assert.That(blockchain.XdcContext.CurrentRound, Is.EqualTo(1UL));
        Assert.That(blockchain.XdcContext.HighestQC.ProposedBlockInfo.Round, Is.EqualTo(0UL));

        IXdcReleaseSpec spec = blockchain.SpecProvider.GetXdcSpec(head, blockchain.XdcContext.CurrentRound);
        Address leader = blockchain.ConsensusModule.GetLeaderAddress(head, blockchain.XdcContext.CurrentRound, spec);
        blockchain.Signer.SetSigner(blockchain.MasterNodeCandidates.First(k => k.Address == leader));
        blockchain.Timestamper.Set(DateTimeOffset.FromUnixTimeSeconds((long)(head.Timestamp + spec.MinePeriod)).UtcDateTime);

        int blocksProposed = 0;
        TaskCompletionSource blockProduced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        blockchain.ConsensusModule.BlockProduced += (_, _) =>
        {
            blocksProposed++;
            blockProduced.TrySetResult();
        };

        blockchain.StartHotStuffModule();

        Task finished = await Task.WhenAny(blockProduced.Task, Task.Delay(10_000));
        if (finished != blockProduced.Task)
            Assert.Fail("Timed out waiting for first block proposal at genesis bootstrap");

        Assert.That(blocksProposed, Is.EqualTo(1));
    }

    [Test]
    public async Task TestSameRoundRestartDuringMineWaitStillProposes()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(useHotStuffModule: true);
        (XdcBlockHeader head, ulong round, ProposalTracker tracker) = await StartLeaderRoundTaskInMineWait(blockchain);

        // Simulates the new-head trigger racing with QC formation: a same-round restart while the
        // proposer is parked in the mine-period wait must not permanently consume the round.
        blockchain.ConsensusModule.StartRoundTask(head, round);

        Task finished = await Task.WhenAny(tracker.FirstProposal.Task, Task.Delay(10_000));
        if (finished != tracker.FirstProposal.Task)
            Assert.Fail("No block was proposed after a same-round restart during the mine-period wait");

        await Task.Delay(500);
        Assert.That(tracker.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task TestStaleRoundTriggerDoesNotCancelCurrentRoundProposal()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(useHotStuffModule: true);
        (XdcBlockHeader head, ulong round, ProposalTracker tracker) = await StartLeaderRoundTaskInMineWait(blockchain);

        // Simulates a stale trigger (racy CurrentRound read or out-of-order round events): an older
        // round must not cancel the in-flight proposal task for the current round.
        blockchain.ConsensusModule.StartRoundTask(head, round - 1);

        Task finished = await Task.WhenAny(tracker.FirstProposal.Task, Task.Delay(10_000));
        if (finished != tracker.FirstProposal.Task)
            Assert.Fail("No block was proposed after a stale-round trigger during the mine-period wait");
    }

    /// <summary>
    /// Makes this node the leader for the current round and starts a round task that is parked in
    /// the mine-period wait (the clock is frozen at the head timestamp, so the wait is a full
    /// <c>MinePeriod</c>), giving the test a window to fire a racing trigger.
    /// </summary>
    private static async Task<(XdcBlockHeader Head, ulong Round, ProposalTracker Tracker)> StartLeaderRoundTaskInMineWait(XdcTestBlockchain blockchain)
    {
        XdcBlockHeader head = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        ulong round = blockchain.XdcContext.CurrentRound;
        IXdcReleaseSpec spec = blockchain.SpecProvider.GetXdcSpec(head, round);
        Address leader = blockchain.ConsensusModule.GetLeaderAddress(head, round, spec);
        blockchain.Signer.SetSigner(blockchain.MasterNodeCandidates.First(k => k.Address == leader));
        blockchain.Timestamper.Set(DateTimeOffset.FromUnixTimeSeconds((long)head.Timestamp).UtcDateTime);

        ProposalTracker tracker = new();
        blockchain.ConsensusModule.BlockProduced += tracker.OnBlockProduced;

        blockchain.StartHotStuffModule();
        blockchain.ConsensusModule.StartRoundTask(head, round);
        // Give the round task time to reach the mine-period wait before the test fires its trigger.
        await Task.Delay(300);
        return (head, round, tracker);
    }

    private sealed class ProposalTracker
    {
        private int _count;
        public TaskCompletionSource FirstProposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count => Volatile.Read(ref _count);
        public void OnBlockProduced(object? sender, BlockEventArgs e)
        {
            Interlocked.Increment(ref _count);
            FirstProposal.TrySetResult();
        }
    }

    [Test]
    public async Task TestStartDoesNotProposeFirstBlockWhenNotLeaderWhileSyncingAtGenesis()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 0, useHotStuffModule: true);
        XdcBlockTree tree = (XdcBlockTree)blockchain.BlockTree;

        XdcBlockHeader head = (XdcBlockHeader)tree.Head!.Header;
        Assert.That(tree.IsSyncing().isSyncing, Is.True);

        IXdcReleaseSpec spec = blockchain.SpecProvider.GetXdcSpec(head, blockchain.XdcContext.CurrentRound);
        Address leader = blockchain.ConsensusModule.GetLeaderAddress(head, blockchain.XdcContext.CurrentRound, spec);
        PrivateKey nonLeader = blockchain.MasterNodeCandidates.First(k => k.Address != leader);
        blockchain.Signer.SetSigner(nonLeader);
        blockchain.Timestamper.Set(DateTimeOffset.FromUnixTimeSeconds((long)(head.Timestamp + spec.MinePeriod)).UtcDateTime);

        int blocksProposed = 0;
        blockchain.ConsensusModule.BlockProduced += (_, _) => blocksProposed++;

        blockchain.StartHotStuffModule();

        await Task.Delay(500);

        Assert.That(blocksProposed, Is.EqualTo(0));
    }
}
