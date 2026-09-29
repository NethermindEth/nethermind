// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Posting;
using Nethermind.Eez.Sequencer;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezSequencerTests
{
    /// <summary>12 s L1, 2 s L2: six blocks a slot, one Future block, four Live ones, composed 8 s after the anchor.</summary>
    private static readonly RollupTiming Timing = new(12_000, 2_000, 2_000, 1_000);

    private IBlockTree _chain = null!;
    private FollowerHeads _heads = null!;
    private FakeSequencedBlocks _blocks = null!;
    private FakeComposer _composer = null!;
    private ManualTimestamper _clock = null!;
    private ulong _genesisTimestamp;

    [SetUp]
    public void SetUp()
    {
        _chain = Build.A.BlockTree().OfChainLength(1).TestObject;
        ChainEngine engine = new(_chain);
        _heads = new FollowerHeads(engine, _chain, _chain.Genesis!);
        _blocks = new FakeSequencedBlocks(engine);
        _composer = new FakeComposer(_blocks);
        _genesisTimestamp = _chain.Genesis!.Timestamp;
        _clock = new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)_genesisTimestamp).UtcDateTime);
    }

    [Test]
    public async Task Advance_BeforeTheSlotIsComposed_ProducesOneLiveBlockPerTick()
    {
        EezSequencer sequencer = Sequencer();
        EezL1Block anchor = Anchor(secondsAfterGenesis: 0);

        await sequencer.Advance(_heads, anchor, CancellationToken.None);
        _clock.Add(TimeSpan.FromSeconds(2));
        await sequencer.Advance(_heads, anchor, CancellationToken.None);

        Assert.That(_heads.Head.Number, Is.EqualTo(2), "a Live block each L2 block time, on the clock");
        Assert.That(_composer.Calls, Is.Empty, "nothing is composed before the proof window opens");
    }

    /// <summary>
    /// The slot anchored at L1 block 10, the genesis's L1 timestamp, has its Sync block at height 6:
    /// 1. at 8 s the missing four Live blocks and the Future block are produced;
    /// 2. the Sync block is composed pinned to L1 block 11 at its timestamp.
    /// </summary>
    [Test]
    public async Task Advance_ProofWindowOpens_ProducesTheMissingBlocksThenPinsTheSyncBlock()
    {
        EezSequencer sequencer = Sequencer();
        EezL1Block anchor = Anchor(secondsAfterGenesis: 0);
        await sequencer.Advance(_heads, anchor, CancellationToken.None);
        Assert.That(_heads.Head.Number, Is.EqualTo(1), "precondition: the first tick produced one Live block");

        _clock.Add(TimeSpan.FromSeconds(8));
        await sequencer.Advance(_heads, anchor, CancellationToken.None);

        Assert.That(_composer.Calls, Has.Count.EqualTo(1), "one Sync block for the slot");
        (BlockHeader parent, BundleTarget target, SyncSlotMode mode) = _composer.Calls[0];
        Assert.That((parent.Number, parent.Timestamp), Is.EqualTo((5UL, _genesisTimestamp + 10)), "the Live blocks still missing and the Future block before it");
        Assert.That((target, mode), Is.EqualTo((new BundleTarget(11, _genesisTimestamp + 12), SyncSlotMode.Steady)),
            "pinned to the next L1 block, whose timestamp the Sync block carries");
    }

    [Test]
    public async Task Advance_ComposedTooLateForAProof_LeavesTheSyncBlockEmpty()
    {
        EezSequencer sequencer = Sequencer();
        _clock.Add(TimeSpan.FromSeconds(10));

        await sequencer.Advance(_heads, Anchor(secondsAfterGenesis: 0), CancellationToken.None);

        Assert.That(_composer.Calls[0].Mode, Is.EqualTo(SyncSlotMode.Empty), "a proof started 2 s before the slot's block cannot land in it");
    }

    [Test]
    public async Task Advance_FarBehindTheSlot_CatchesUpToASyncBlockOnTheGrid()
    {
        EezSequencer sequencer = Sequencer();
        _clock.Add(TimeSpan.FromSeconds(1_208));

        await sequencer.Advance(_heads, Anchor(secondsAfterGenesis: 1_200), CancellationToken.None);

        (BlockHeader parent, BundleTarget target, SyncSlotMode mode) = _composer.Calls[0];
        Assert.That((parent.Number + 1) % Timing.K, Is.Zero, "the catch-up ends at a Sync height derivation rebuilds by position");
        Assert.That(parent.Number + 1, Is.LessThanOrEqualTo(RollupTiming.MaxBlocksPerCatchup), "one catch-up settles in one batch");
        Assert.That((target, mode), Is.EqualTo((BundleTarget.NextBlock, SyncSlotMode.Catchup)), "past blocks settle in whichever L1 block takes them");
    }

    [Test]
    public async Task Advance_HeadOffTheSlotGrid_SkipsTheSyncBlock()
    {
        EezSequencer sequencer = Sequencer();
        _blocks.TimestampStep = 3;
        _clock.Add(TimeSpan.FromSeconds(8));

        await sequencer.Advance(_heads, Anchor(secondsAfterGenesis: 0), CancellationToken.None);

        Assert.That(_composer.Calls, Is.Empty, "a Sync block whose timestamp is not the next L1 block's cannot settle in it");
    }

    [Test]
    public async Task Advance_AtTheSpeculativeDepth_WaitsForL1()
    {
        EezSequencer sequencer = Sequencer(maxDepth: 3);
        _clock.Add(TimeSpan.FromSeconds(8));

        await sequencer.Advance(_heads, Anchor(secondsAfterGenesis: 0), CancellationToken.None);

        Assert.That((_heads.Head.Number, _composer.Calls.Count), Is.EqualTo((3UL, 0)), "no more than the depth above what L1 settled, and no Sync block past it");
    }

    [Test]
    public async Task Advance_NewerL1BlockBeforeComposition_ReplacesTheSlot()
    {
        EezSequencer sequencer = Sequencer();
        await sequencer.Advance(_heads, Anchor(secondsAfterGenesis: 0), CancellationToken.None);
        _clock.Add(TimeSpan.FromSeconds(20));

        await sequencer.Advance(_heads, Anchor(secondsAfterGenesis: 12, number: 11), CancellationToken.None);

        Assert.That(_composer.Calls[0].Parent.Number + 1, Is.EqualTo(12), "the newest L1 block anchors the slot, whose Sync block is at height 12");
    }

    private EezSequencer Sequencer(ulong maxDepth = 0) => new(Timing, _genesisTimestamp, _blocks, _composer, _clock, maxDepth, LimboLogs.Instance);

    private EezL1Block Anchor(ulong secondsAfterGenesis, ulong number = 10) =>
        new() { Number = number, Hash = Keccak.Compute($"L1 block {number}"), Timestamp = _genesisTimestamp + secondsAfterGenesis };

    private sealed class FakeComposer(FakeSequencedBlocks blocks) : ISyncSlotComposer
    {
        public List<(BlockHeader Parent, BundleTarget Target, SyncSlotMode Mode)> Calls { get; } = [];

        public Task<BlockHeader> Compose(BlockHeader parent, BundleTarget target, SyncSlotMode mode, FollowerHeads heads, CancellationToken token)
        {
            Calls.Add((parent, target, mode));
            return blocks.Empty(parent, heads);
        }
    }
}
