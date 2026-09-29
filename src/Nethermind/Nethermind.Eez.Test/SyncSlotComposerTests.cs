// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Posting;
using Nethermind.Eez.Proving;
using Nethermind.Eez.Sequencer;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class SyncSlotComposerTests
{
    private const ulong RollupId = 2;
    private const ulong SettledIn = 11;
    private static readonly RollupTiming Timing = new(12_000, 2_000, 2_000, 1_000);
    private static readonly BundleTarget Pinned = new(11, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3_600);
    private static readonly Address ProofSystem = new("0x00000000000000000000000000000000000000e7");
    private static readonly ValueHash256 VerificationKey = Keccak.Compute("vkey").ValueHash256;

    private IBlockTree _chain = null!;
    private FollowerHeads _heads = null!;
    private FakeSequencedBlocks _blocks = null!;
    private OptimisticLedger _ledger = null!;
    private IWitnessRecorder _witnesses = null!;
    private IQuorumRegistrationReader _registrations = null!;
    private IPostBatchPoster _poster = null!;
    private TaskCompletionSource<PostResult> _observed = null!;

    [SetUp]
    public void SetUp()
    {
        _chain = Build.A.BlockTree().OfChainLength(1).TestObject;
        ChainEngine engine = new(_chain);
        _heads = new FollowerHeads(engine, _chain, _chain.Genesis!);
        _blocks = new FakeSequencedBlocks(engine);
        _ledger = new OptimisticLedger();
        _witnesses = Substitute.For<IWitnessRecorder>();
        _witnesses.Get(Arg.Any<BlockHeader>(), Arg.Any<Block>()).Returns(static _ => EmptyWitness());
        _registrations = Substitute.For<IQuorumRegistrationReader>();
        _registrations.Read(Arg.Any<IReadOnlyList<IAttester>>(), Arg.Any<CancellationToken>()).Returns(new QuorumRegistration(1, [VerificationKey]));
        _observed = new TaskCompletionSource<PostResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _poster = Substitute.For<IPostBatchPoster>();
        _poster.Sign(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(static call => new SignedPostBatch(call.ArgAt<byte[]>(0), Keccak.Compute(call.ArgAt<byte[]>(0)), 0));
        _poster.Submit(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<BundleTarget>(), Arg.Any<CancellationToken>()).Returns(SettledIn);
        _poster.Observe(Arg.Any<Hash256>(), Arg.Any<ulong>(), Arg.Any<BundleTarget>(), Arg.Any<ValueHash256>(), Arg.Any<CancellationToken>()).Returns(_ => _observed.Task);
    }

    [Test]
    public async Task Compose_OnTime_PostsTheBatchSettlingEverythingSinceTheCursor()
    {
        BlockHeader parent = await _blocks.Live(_heads.Head, _heads, CancellationToken.None);

        BlockHeader sync = await Composer().Compose(parent, SlotPlan.Steady(Pinned, 10), _heads, CancellationToken.None);

        Assert.That(sync.Number, Is.EqualTo(2), "the Sync block follows its parent");
        Assert.That(ProvenBlocks(), Is.EqualTo(new ulong[] { 1, 2 }), "the batch settles every block after the cursor up to the Sync block");
        await _poster.Received(1).Submit(Arg.Any<IReadOnlyList<byte[]>>(), Pinned, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Compose_CatchingUp_PostsToWhicheverBlockTakesIt()
    {
        await Composer().Compose(_heads.Head, SlotPlan.Catchup(10), _heads, CancellationToken.None);

        await _poster.Received(1).Submit(Arg.Any<IReadOnlyList<byte[]>>(), BundleTarget.NextBlock, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Compose_BatchInFlight_NextSlotPostsNothingFromTheSameCursor()
    {
        SyncSlotComposer composer = Composer();
        await composer.Compose(_heads.Head, SlotPlan.Steady(Pinned, 10), _heads, CancellationToken.None);

        BlockHeader next = await composer.Compose(_heads.Head, SlotPlan.Steady(Pinned, 10), _heads, CancellationToken.None);

        await _poster.Received(1).Sign(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        Assert.That(_blocks.Empties.Last(), Is.EqualTo(next), "the slot still gets its Sync block; a second batch from the cursor would revert");
    }

    [TestCase(SettledIn, 2, TestName = "FollowerReadItsBlock")]
    [TestCase(SettledIn - 1, 1, TestName = "FollowerNotThereYet")]
    public async Task Compose_ObserverSawItSettleButTheCursorNeverReachedIt_PostsAgainOnceTheFollowerReadThatBlock(ulong followed, int posted)
    {
        _observed.SetResult(new PostResult(PostOutcome.Settled, SettledIn));
        SyncSlotComposer composer = Composer();
        await composer.Compose(_heads.Head, SlotPlan.Steady(Pinned, 10), _heads, CancellationToken.None);

        await composer.Compose(_heads.Head, SlotPlan.Steady(Pinned, followed), _heads, CancellationToken.None);
        await composer.Compose(_heads.Head, SlotPlan.Steady(Pinned, followed), _heads, CancellationToken.None);

        await _poster.Received(posted).Sign(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Compose_CursorReorganizedBelowASettledBatch_PostsItAgain()
    {
        BlockHeader first = await _blocks.Live(_heads.Head, _heads, CancellationToken.None);
        await _heads.AdvanceSafe(first);
        _observed.SetResult(new PostResult(PostOutcome.Settled, SettledIn));
        SyncSlotComposer composer = Composer();
        await composer.Compose(_heads.Head, SlotPlan.Steady(Pinned, 10), _heads, CancellationToken.None);

        await _heads.RetreatSafe(_chain.Genesis!);
        await composer.Compose(_heads.Head, SlotPlan.Steady(Pinned, 10), _heads, CancellationToken.None);

        await _poster.Received(2).Sign(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [TestCase(SyncSlotMode.Empty, false, TestName = "LateSlot")]
    [TestCase(SyncSlotMode.Steady, true, TestName = "BatchStillAboveTheCursor")]
    public async Task Compose_NothingToPost_StillProducesTheSyncBlock(SyncSlotMode mode, bool unsettledBatch)
    {
        if (unsettledBatch)
        {
            _ledger.Begin(new PostedBatch(6, Keccak.Compute("posted"), _chain.Genesis!));
        }

        BlockHeader sync = await Composer().Compose(_heads.Head, new SlotPlan(mode, Pinned, 10), _heads, CancellationToken.None);

        Assert.That(sync.Number, Is.EqualTo(1), "L2 cadence does not depend on settlement");
        Assert.That(ProvenBlocks(), Is.Empty, "nothing is proven for a slot that posts nothing");
    }

    [Test]
    public async Task Compose_FailedBatchAboveTheCursor_IsRecoveredWithAnEmptySlot()
    {
        _ledger.Begin(new PostedBatch(6, Keccak.Compute("posted"), _chain.Genesis!));
        _ledger.MarkFailed(6, slotSkipped: false);
        SyncSlotComposer composer = Composer();

        BlockHeader recovery = await composer.Compose(_heads.Head, SlotPlan.Steady(Pinned, 10), _heads, CancellationToken.None);
        await composer.Compose(_heads.Head, SlotPlan.Steady(Pinned, 10), _heads, CancellationToken.None);

        Assert.That(recovery.Number, Is.EqualTo(1), "precondition: the recovery slot still has its Sync block");
        Assert.That(ProvenBlocks(), Is.EqualTo(new ulong[] { 1, 2 }), "the slot after the recovery settles from the cursor again");
    }

    /// <summary>
    /// Thirteen blocks above the cursor and a batch cap of one slot:
    /// 1. the Sync block at 14 is produced with nothing posted for it;
    /// 2. the oldest chunk is settled up to height 2, the Sync height on 14's grid within the cap.
    /// </summary>
    [Test]
    public async Task Compose_BacklogOverTheBlockCap_SettlesTheOldestChunkOnTheGrid()
    {
        BlockHeader head = await Produce(13);

        await Composer(maxBlocksPerBatch: 6).Compose(head, SlotPlan.Steady(Pinned, 10), _heads, CancellationToken.None);

        Assert.That(ProvenBlocks(), Is.EqualTo(new ulong[] { 1, 2 }), "the chunk ends at a Sync height derivation rebuilds by position");
    }

    /// <summary>
    /// Two slots of blocks carrying calldata above the cursor, with a gas limit that fits one slot's batch and not two:
    /// the first slot is settled on its own, ending on the Sync block's grid, to whichever L1 block takes it.
    /// </summary>
    [Test]
    public async Task Compose_BatchOverTheGasLimit_SettlesTheLongestPrefixThatFitsOnTheGrid()
    {
        _blocks.CalldataPerBlock = 4_000;
        BlockHeader head = await Produce(11);
        ulong oneSlot = Gas(6);
        Assert.That(Gas(11), Is.GreaterThan(oneSlot), "precondition: the two slots need more gas than one");

        await Composer(maxPostBatchGas: oneSlot).Compose(head, SlotPlan.Steady(Pinned, 10), _heads, CancellationToken.None);

        Assert.That(ProvenBlocks(), Is.EqualTo(new ulong[] { 1, 2, 3, 4, 5, 6 }), "one slot, ending on the grid, within one batch's gas");
        await _poster.Received(1).Submit(Arg.Any<IReadOnlyList<byte[]>>(), BundleTarget.NextBlock, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Compose_EverySlot_ForgetsTheWitnessesOfFinalizedBlocks()
    {
        BlockHeader finalized = await _blocks.Live(_heads.Head, _heads, CancellationToken.None);
        await _heads.AdvanceSafe(finalized);
        await _heads.AdvanceFinalized(finalized);

        await Composer().Compose(_heads.Head, SlotPlan.Steady(Pinned, 10), _heads, CancellationToken.None);

        _witnesses.Received(1).ForgetThrough(1);
    }

    private SyncSlotComposer Composer(ulong maxBlocksPerBatch = 300, ulong maxPostBatchGas = PostBatchGas.DefaultLimit) =>
        new(_chain, _blocks, _witnesses, _ledger, new AttestationQuorum([Signing()], new ProveRetry(Timing, Timestamper.Default), TimeSpan.Zero, LimboLogs.Instance),
            _registrations, _poster, Substitute.For<IBatchCheck>(), DerivedChain.Context with { RollupId = RollupId }, Timing,
            new ComposerSettings(maxBlocksPerBatch, maxPostBatchGas), LimboLogs.Instance);

    private async Task<BlockHeader> Produce(int count)
    {
        BlockHeader head = _heads.Head;
        for (int i = 0; i < count; i++)
        {
            head = await _blocks.Live(head, _heads, CancellationToken.None);
        }

        return head;
    }

    /// <summary>The gas of the anchor batch settling the first <paramref name="count"/> blocks after genesis.</summary>
    private ulong Gas(int count)
    {
        List<Block> span = [.. Enumerable.Range(1, count).Select(n => _chain.FindBlock(_chain.FindHeader((ulong)n, BlockTreeLookupOptions.None)!.Hash!, BlockTreeLookupOptions.None)!)];
        PostBatch batch = AnchorBatch.Build(RollupId, _chain.Genesis!.Hash!, span[^1].Hash!, AnchorBatch.Da(span), [ProofSystem]);
        return PostBatchGas.Needed(batch, 1);
    }

    private ulong[] ProvenBlocks() =>
        [.. _witnesses.ReceivedCalls().Where(static c => c.GetMethodInfo().Name == nameof(IWitnessRecorder.Get)).Select(static c => ((Block)c.GetArguments()[1]!).Number)];

    /// <summary>An attester that signs the public inputs hash its batch has under the registered key.</summary>
    private static IAttester Signing()
    {
        IAttester attester = Substitute.For<IAttester>();
        attester.ProofSystem.Returns(ProofSystem);
        attester.Signer.Returns(TestItem.PrivateKeyA.Address);
        attester.Prove(Arg.Any<ProveRequest>(), Arg.Any<CancellationToken>()).Returns(static call =>
        {
            ProveRequest own = call.ArgAt<ProveRequest>(0);
            return Task.FromResult(new EezAttestationSigner(TestItem.PrivateKeyA).Sign(PostBatchProfile.PublicInputsHash(own.Batch, own.RollupId, VerificationKey)));
        });
        return attester;
    }

    private static Witness EmptyWitness() => new()
    {
        State = new ArrayPoolList<byte[]>(0),
        Codes = new ArrayPoolList<byte[]>(0),
        Keys = new ArrayPoolList<byte[]>(0),
        Headers = new ArrayPoolList<byte[]>(0),
    };
}
