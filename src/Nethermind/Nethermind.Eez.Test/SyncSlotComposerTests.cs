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
    private static readonly RollupTiming Timing = new(12_000, 2_000, 2_000, 1_000);
    private static readonly BundleTarget Pinned = new(11, 1_000);

    private IBlockTree _chain = null!;
    private FollowerHeads _heads = null!;
    private FakeSequencedBlocks _blocks = null!;
    private OptimisticLedger _ledger = null!;
    private IWitnessRecorder _witnesses = null!;
    private IQuorumRegistrationReader _registrations = null!;

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
        _registrations.Read(Arg.Any<IReadOnlyList<IAttester>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<QuorumRegistration>(new ProveException(ProveFailureKind.Retryable, "L1 unavailable")));
    }

    [Test]
    public async Task Compose_OnTime_SettlesEverythingSinceTheCursor()
    {
        BlockHeader parent = await _blocks.Live(_heads.Head, _heads, CancellationToken.None);

        BlockHeader sync = await Composer().Compose(parent, Pinned, SyncSlotMode.Steady, _heads, CancellationToken.None);

        Assert.That(sync.Number, Is.EqualTo(2), "the Sync block follows its parent");
        Assert.That(ProvenBlocks(), Is.EqualTo(new ulong[] { 1, 2 }), "the batch settles every block after the cursor up to the Sync block");
    }

    [TestCase(SyncSlotMode.Empty, false, TestName = "LateSlot")]
    [TestCase(SyncSlotMode.Steady, true, TestName = "BatchStillAboveTheCursor")]
    public async Task Compose_NothingToPost_StillProducesTheSyncBlock(SyncSlotMode mode, bool unsettledBatch)
    {
        if (unsettledBatch)
        {
            _ledger.Begin(new PostedBatch(6, Keccak.Compute("posted"), _chain.Genesis!));
        }

        BlockHeader sync = await Composer().Compose(_heads.Head, Pinned, mode, _heads, CancellationToken.None);

        Assert.That(sync.Number, Is.EqualTo(1), "L2 cadence does not depend on settlement");
        Assert.That(ProvenBlocks(), Is.Empty, "nothing is proven for a slot that posts nothing");
    }

    [Test]
    public async Task Compose_FailedBatchAboveTheCursor_IsRecoveredWithAnEmptySlot()
    {
        _ledger.Begin(new PostedBatch(6, Keccak.Compute("posted"), _chain.Genesis!));
        _ledger.MarkFailed(6, slotSkipped: false);
        SyncSlotComposer composer = Composer();

        await composer.Compose(_heads.Head, Pinned, SyncSlotMode.Steady, _heads, CancellationToken.None);
        BlockHeader next = await composer.Compose(_heads.Head, Pinned, SyncSlotMode.Steady, _heads, CancellationToken.None);

        Assert.That(ProvenBlocks(), Is.EqualTo(new ulong[] { 1, 2 }), "the slot after the recovery settles from the cursor again");
        Assert.That(next.Number, Is.EqualTo(2), "precondition: one Sync block a slot");
    }

    /// <summary>
    /// Thirteen blocks above the cursor and a batch cap of one slot:
    /// 1. the Sync block at 14 is produced with nothing posted for it;
    /// 2. the oldest chunk is settled up to height 2, the Sync height on 14's grid within the cap.
    /// </summary>
    [Test]
    public async Task Compose_BacklogOverTheCap_SettlesTheOldestChunkOnTheGrid()
    {
        BlockHeader head = _heads.Head;
        for (int i = 0; i < 13; i++)
        {
            head = await _blocks.Live(head, _heads, CancellationToken.None);
        }

        await Composer(maxBlocksPerBatch: 6).Compose(head, Pinned, SyncSlotMode.Steady, _heads, CancellationToken.None);

        Assert.That(ProvenBlocks(), Is.EqualTo(new ulong[] { 1, 2 }), "the chunk ends at a Sync height derivation rebuilds by position");
    }

    private SyncSlotComposer Composer(ulong maxBlocksPerBatch = 300) =>
        new(_chain, _blocks, _witnesses, _ledger, new AttestationQuorum([], new ProveRetry(Timing, Timestamper.Default), TimeSpan.Zero, LimboLogs.Instance),
            _registrations, Substitute.For<IPostBatchPoster>(), DerivedChain.SpecProvider, DerivedChain.Context, Timing,
            new ComposerSettings(maxBlocksPerBatch, PostBatchGas.DefaultLimit), LimboLogs.Instance);

    private ulong[] ProvenBlocks() =>
        [.. _witnesses.ReceivedCalls().Where(static c => c.GetMethodInfo().Name == nameof(IWitnessRecorder.Get)).Select(static c => ((Block)c.GetArguments()[1]!).Number)];

    private static Witness EmptyWitness() => new()
    {
        State = new ArrayPoolList<byte[]>(0),
        Codes = new ArrayPoolList<byte[]>(0),
        Keys = new ArrayPoolList<byte[]>(0),
        Headers = new ArrayPoolList<byte[]>(0),
    };
}
