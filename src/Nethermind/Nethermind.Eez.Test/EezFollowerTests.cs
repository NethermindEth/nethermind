// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Sequencer;
using Nethermind.Logging;
using Nethermind.Specs;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezFollowerTests
{
    private const ulong L1ChainId = 3151908;
    private const ulong RollupId = 1;
    private const ulong SettlingL1Block = 3;
    private static readonly Address Registry = new("0x5fbdb2315678afecb367f032d93f642f64180aa3");

    private readonly Dictionary<ulong, Hash256> _l1Chain = [];
    private readonly List<BlockHeader> _cursors = [];
    private IEezL1Api _l1 = null!;
    private IL1BatchScanner _scanner = null!;
    private IBatchReconciler _reconciler = null!;
    private IUnsafeHeadSource _unsafeHead = null!;
    private IBlockTree _chain = null!;
    private ChainEngine _engine = null!;
    private EezConfig _config = null!;
    private EezFollower _follower = null!;

    [SetUp]
    public void SetUp()
    {
        _l1Chain.Clear();
        _cursors.Clear();
        _chain = Build.A.BlockTree().OfChainLength(6).TestObject;
        _engine = new ChainEngine(_chain);
        _config = new EezConfig { L1ChainId = L1ChainId, RegistryAddress = Registry.ToString(), RegistryDeployBlock = 1, RollupId = RollupId, L1LogScanBlocks = 100 };
        _l1 = Substitute.For<IEezL1Api>();
        _l1.GetChainId(Arg.Any<CancellationToken>()).Returns(L1ChainId);
        _l1.GetBlockByNumber(Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(call => _l1Chain.TryGetValue(call.ArgAt<ulong>(0), out Hash256? hash) ? new EezL1Block { Number = call.ArgAt<ulong>(0), Hash = hash, MixHash = Keccak.Zero } : null);
        _l1.GetLatestBlock(Arg.Any<CancellationToken>()).Returns(_ => L1Block(_l1Chain.Keys.Max()));
        GrowL1(to: 5, fork: "a");

        _scanner = Substitute.For<IL1BatchScanner>();
        _scanner.FindSettlingBlocks(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<ulong>(0) <= SettlingL1Block && SettlingL1Block <= call.ArgAt<ulong>(1) ? [Settling(SettlingL1Block)] : []);
        _scanner.Scan(Arg.Any<L1SettlingBlock>(), Arg.Any<EezL1Block>(), Arg.Any<CancellationToken>()).Returns(call => new[] { Batch(call.ArgAt<L1SettlingBlock>(0)) });
        _reconciler = Substitute.For<IBatchReconciler>();
        SettleTo(2);

        _unsafeHead = Substitute.For<IUnsafeHeadSource>();
        _follower = Follower(_engine);
    }

    [Test]
    public async Task Poll_L1BlockSettlesTheRollup_MovesSafeToItsEnd()
    {
        await _follower.Boot(CancellationToken.None);

        await _follower.Poll(CancellationToken.None);

        Assert.That(_follower.Heads.Safe.Number, Is.EqualTo(2), "safe is the L2 block L1's commitment names after the settling block");
        Assert.That(_engine.Forkchoices[^1].Safe, Is.EqualTo(L2(2).Hash), "the node's own safe head moved with it");
    }

    [Test]
    public async Task Poll_FinalizedL1BlockHoldsASettlement_MovesFinalized()
    {
        _l1.GetFinalizedBlock(Arg.Any<CancellationToken>()).Returns(L1Block(SettlingL1Block));
        await _follower.Boot(CancellationToken.None);

        await _follower.Poll(CancellationToken.None);

        Assert.That(_follower.Heads.Finalized.Number, Is.EqualTo(2), "a settlement in a finalized L1 block finalizes its L2 block");
    }

    [Test]
    public async Task Poll_FinalizationEngineBusyOnce_RetriesWithoutAnotherSettlement()
    {
        _l1.GetFinalizedBlock(Arg.Any<CancellationToken>()).Returns(L1Block(SettlingL1Block));
        IEezL2Engine engine = Substitute.For<IEezL2Engine>();
        int attempts = 0;
        engine.UpdateForkchoice(Arg.Any<Hash256>(), Arg.Any<Hash256>(), Arg.Any<Hash256>()).Returns(call =>
        {
            if (call.ArgAt<Hash256>(2) == L2(2).Hash && ++attempts == 1)
            {
                return Task.FromException(new EezEngineUnavailableException("busy"));
            }

            return _engine.UpdateForkchoice(call.ArgAt<Hash256>(0), call.ArgAt<Hash256>(1), call.ArgAt<Hash256>(2));
        });
        _follower = Follower(engine);
        await _follower.Boot(CancellationToken.None);

        FollowerProgress first = await _follower.Poll(CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(FollowerProgress.Retry));
            Assert.That(_follower.Heads.Safe.Number, Is.EqualTo(2));
            Assert.That(_follower.Heads.Finalized.Number, Is.Zero);
        }

        FollowerProgress second = await _follower.Poll(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.EqualTo(FollowerProgress.CaughtUp));
            Assert.That(_follower.Heads.Finalized.Number, Is.EqualTo(2));
            Assert.That(attempts, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task Poll_SettlementAboveFinalizedL1_KeepsFinalized()
    {
        _l1.GetFinalizedBlock(Arg.Any<CancellationToken>()).Returns(L1Block(SettlingL1Block - 1));
        await _follower.Boot(CancellationToken.None);

        await _follower.Poll(CancellationToken.None);

        Assert.That(_follower.Heads.Finalized.Number, Is.Zero, "L1 has not finalized the block that settled it");
    }

    /// <summary>
    /// L1 reorganizes the settling block away:
    /// 1. the next poll sees the last scanned L1 block changed;
    /// 2. the follower resumes from the settlement that survived, and safe and the head move back to it.
    /// </summary>
    [Test]
    public async Task Poll_L1Reorganized_ResumesFromTheLastSurvivingSettlement()
    {
        _l1.GetLogs(Registry, L1BatchScanner.L2ExecutionPerformedTopic, L1BatchScanner.RollupTopic(RollupId), Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(_ => [SettledRoot(l1Block: 2, l2Block: 1)]);
        await _follower.Boot(CancellationToken.None);
        await _follower.Poll(CancellationToken.None);
        Assert.That(_follower.Heads.Safe.Number, Is.EqualTo(2), "precondition: L1 block 3 settled L2 block 2");
        GrowL1(to: 5, fork: "b", from: SettlingL1Block);
        _scanner.FindSettlingBlocks(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Returns([]);

        await _follower.Poll(CancellationToken.None);

        Assert.That((_follower.Heads.Safe.Number, _follower.Heads.Head.Number), Is.EqualTo((1UL, 1UL)), "safe and the head return to the surviving settlement");
        Assert.That(_scanner.ReceivedCalls().Any(static call => call.GetMethodInfo().Name == nameof(IL1BatchScanner.FindSettlingBlocks)
            && (ulong)call.GetArguments()[0]! == SettlingL1Block), Is.True, "the scan starts over at the reorganized block");
    }

    /// <summary>
    /// Two batches settle the rollup in one L1 block and the engine is busy for the second:
    /// 1. the first batch moved the cursor to L2 block 1, the second fails;
    /// 2. the next poll starts the block over from the cursor before it, not from the first batch's end.
    /// </summary>
    [Test]
    public async Task Poll_EngineBusyMidBlock_RetriesTheBlockFromItsCursor()
    {
        _scanner.Scan(Arg.Any<L1SettlingBlock>(), Arg.Any<EezL1Block>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { Batch(call.ArgAt<L1SettlingBlock>(0)), Batch(call.ArgAt<L1SettlingBlock>(0)) });
        _reconciler.Reconcile(Arg.Any<ScannedBatch>(), Arg.Any<L1Anchor>(), Arg.Any<SettledHead>(), Arg.Any<FollowerHeads>())
            .Returns(call => Settled(call, 1), call => Busy(call), call => Settled(call, 1), call => Settled(call, 2));
        await _follower.Boot(CancellationToken.None);

        FollowerProgress progress = await _follower.Poll(CancellationToken.None);
        await _follower.Poll(CancellationToken.None);

        Assert.That(progress, Is.EqualTo(FollowerProgress.Retry), "a failed poll waits before the next one");
        Assert.That(_cursors.Select(static c => c.Number), Is.EqualTo(new ulong[] { 0, 1, 0, 1 }), "the retried block starts over from the cursor before it");
        Assert.That(_follower.Heads.Safe.Number, Is.EqualTo(2), "the retry settles the block");
    }

    [Test]
    public async Task Poll_DivergenceOnABlockL1Reorganized_IsRetried()
    {
        _reconciler.Reconcile(Arg.Any<ScannedBatch>(), Arg.Any<L1Anchor>(), Arg.Any<SettledHead>(), Arg.Any<FollowerHeads>()).Returns(_ =>
        {
            GrowL1(to: 5, fork: "b", from: SettlingL1Block);
            return Task.FromException<SettledHead>(new EezFollowerException("diverges"));
        });
        await _follower.Boot(CancellationToken.None);

        await Assert.DoesNotThrowAsync(() => _follower.Poll(CancellationToken.None), "a batch L1 reorganized away while it was derived is not a divergence");
    }

    [Test]
    public async Task Poll_DivergenceOnACanonicalBlock_Throws()
    {
        _reconciler.Reconcile(Arg.Any<ScannedBatch>(), Arg.Any<L1Anchor>(), Arg.Any<SettledHead>(), Arg.Any<FollowerHeads>())
            .Returns(Task.FromException<SettledHead>(new EezFollowerException("diverges")));
        await _follower.Boot(CancellationToken.None);

        await Assert.ThrowsAsync<EezFollowerException>(() => _follower.Poll(CancellationToken.None), "a settlement L1 still holds that the chain cannot reproduce stops the follower");
    }

    [Test]
    public async Task Poll_LatestL1BlockUnchanged_ReadsOnlyTheLatest()
    {
        await _follower.Boot(CancellationToken.None);
        await _follower.Poll(CancellationToken.None);
        _l1.ClearReceivedCalls();
        _scanner.ClearReceivedCalls();

        await _follower.Poll(CancellationToken.None);

        Assert.That(_l1.ReceivedCalls().Select(static call => call.GetMethodInfo().Name), Is.EqualTo(new[] { nameof(IEezL1Api.GetLatestBlock) }),
            "an unchanged L1 head cannot hide a reorganization, new settlements or a new finalized block");
        Assert.That(_scanner.ReceivedCalls(), Is.Empty, "nothing new on L1 means nothing to scan");
    }

    [Test]
    public async Task Poll_MoreL1BlocksThanOneScan_ReportsItIsBehind()
    {
        _config.L1LogScanBlocks = 2;
        await _follower.Boot(CancellationToken.None);

        FollowerProgress progress = await _follower.Poll(CancellationToken.None);

        Assert.That(progress, Is.EqualTo(FollowerProgress.Behind), "a follower catching up polls again without waiting");
    }

    [Test]
    public async Task Poll_SequencerUnreachable_StillFollowsL1()
    {
        _unsafeHead.Advance(Arg.Any<FollowerHeads>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new HttpRequestException("refused")));
        await _follower.Boot(CancellationToken.None);

        await _follower.Poll(CancellationToken.None);

        Assert.That(_follower.Heads.Safe.Number, Is.EqualTo(2), "a sequencer failure never holds back what L1 settled");
    }

    [Test]
    public async Task Boot_L1OnAnotherChain_Throws() =>
        await Assert.ThrowsAsync<EezFollowerException>(() =>
        {
            _l1.GetChainId(Arg.Any<CancellationToken>()).Returns(L1ChainId + 1);
            return _follower.Boot(CancellationToken.None);
        }, "following another L1 would derive a chain that was never settled");

    [Test]
    public async Task Step_FollowerCaughtUp_LetsTheSequencerAct()
    {
        IEezSequencer sequencer = Substitute.For<IEezSequencer>();
        await using EezDriver driver = Driver(sequencer);
        await _follower.Boot(CancellationToken.None);

        await driver.Step(default, CancellationToken.None);

        await sequencer.Received(1).Advance(_follower.Heads, Arg.Is<EezL1Block?>(static b => b!.Value.Number == 5), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Step_FailedScan_HoldsSequencerThroughBackoffUntilScanSucceeds()
    {
        ManualTimestamper clock = new(DateTime.UnixEpoch.AddSeconds(1_000));
        _config.L1PollingIntervalMs = 2_000;
        IEezSequencer sequencer = Substitute.For<IEezSequencer>();
        await using EezDriver driver = Driver(sequencer, clock: clock);
        await _follower.Boot(CancellationToken.None);
        await driver.Step(default, CancellationToken.None);
        sequencer.ClearReceivedCalls();
        GrowL1(to: 6, fork: "a");
        _scanner.FindSettlingBlocks(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<L1SettlingBlock[]>(new HttpRequestException("unavailable")));

        (DateTimeOffset nextPoll, DateTimeOffset wake) = await driver.Step(default, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_follower.LatestL1!.Value.Number, Is.EqualTo(6));
            Assert.That((nextPoll, wake), Is.EqualTo((clock.UtcNowOffset.AddSeconds(2), clock.UtcNowOffset.AddSeconds(2))));
            Assert.That(sequencer.ReceivedCalls(), Is.Empty);
        }

        await driver.Step(nextPoll, CancellationToken.None);
        Assert.That(sequencer.ReceivedCalls(), Is.Empty, "sequencing remains disabled between polls after a failed scan");
        _scanner.FindSettlingBlocks(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Returns([]);
        clock.Add(TimeSpan.FromSeconds(2));

        await driver.Step(nextPoll, CancellationToken.None);

        await sequencer.Received(1).Advance(_follower.Heads, Arg.Is<EezL1Block?>(static b => b!.Value.Number == 6), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Step_FollowerBehindL1_HoldsTheSequencer()
    {
        IEezSequencer sequencer = Substitute.For<IEezSequencer>();
        _config.L1LogScanBlocks = 2;
        ManualTimestamper clock = new(DateTime.UnixEpoch.AddSeconds(1_000));
        await using EezDriver driver = Driver(sequencer, clock: clock);
        await _follower.Boot(CancellationToken.None);

        (DateTimeOffset nextPoll, DateTimeOffset wake) = await driver.Step(default, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sequencer.ReceivedCalls(), Is.Empty, "a batch posted from a cursor L1 already moved past would revert");
            Assert.That((nextPoll, wake), Is.EqualTo((clock.UtcNowOffset, clock.UtcNowOffset)));
        }
    }

    [TestCase(300, 300, TestName = "SequencerDueFirst")]
    [TestCase(5_000, 2_000, TestName = "PollDueFirst")]
    public async Task Step_CaughtUp_WakesForWhicheverIsDueFirst(int sequencerDueMs, int wakeMs)
    {
        ManualTimestamper clock = new(DateTime.UnixEpoch.AddSeconds(1_000));
        DateTimeOffset now = clock.UtcNowOffset;
        _config.L1PollingIntervalMs = 2_000;
        IEezSequencer sequencer = Substitute.For<IEezSequencer>();
        sequencer.Advance(Arg.Any<FollowerHeads>(), Arg.Any<EezL1Block?>(), Arg.Any<CancellationToken>()).Returns(now + TimeSpan.FromMilliseconds(sequencerDueMs));
        await using EezDriver driver = Driver(sequencer, clock: clock);
        await _follower.Boot(CancellationToken.None);

        (DateTimeOffset nextPoll, DateTimeOffset wake) = await driver.Step(default, CancellationToken.None);

        Assert.That((nextPoll, wake), Is.EqualTo((now + TimeSpan.FromSeconds(2), now + TimeSpan.FromMilliseconds(wakeMs))),
            "L1 is polled on its interval and a slot is composed on time, not at the next poll");
    }

    [Test]
    public async Task Step_BeforeTheNextPoll_LeavesL1Alone()
    {
        ManualTimestamper clock = new(DateTime.UnixEpoch.AddSeconds(1_000));
        IEezSequencer sequencer = Substitute.For<IEezSequencer>();
        await using EezDriver driver = Driver(sequencer, clock: clock);
        await _follower.Boot(CancellationToken.None);
        (DateTimeOffset nextPoll, _) = await driver.Step(default, CancellationToken.None);
        _l1.ClearReceivedCalls();
        sequencer.ClearReceivedCalls();

        await driver.Step(nextPoll, CancellationToken.None);

        await _l1.DidNotReceiveWithAnyArgs().GetLatestBlock(default);
        await sequencer.Received(1).Advance(_follower.Heads, Arg.Any<EezL1Block?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Step_SequencerMissesL1ForAMoment_RetriesOnTheNextStep()
    {
        IEezSequencer sequencer = Substitute.For<IEezSequencer>();
        sequencer.Advance(Arg.Any<FollowerHeads>(), Arg.Any<EezL1Block?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DateTimeOffset>(new L1SourceIncompleteException(5, "not served")));
        await using EezDriver driver = Driver(sequencer);
        await _follower.Boot(CancellationToken.None);

        await Assert.DoesNotThrowAsync(() => driver.Step(default, CancellationToken.None), "an L1 read that fails now is retried, not a reason to stop the node");
    }

    [Test]
    public async Task Start_L1OnAnotherChain_StopsTheNode()
    {
        _l1.GetChainId(Arg.Any<CancellationToken>()).Returns(L1ChainId + 1);
        IProcessExitSource processExit = Substitute.For<IProcessExitSource>();
        await using EezDriver driver = Driver(null, processExit);

        await driver.Start();

        processExit.Received(1).Exit(ExitCodes.InvalidBlock);
    }

    [Test]
    public async Task DisposeAsync_Twice_DoesNotThrow()
    {
        EezDriver driver = Driver(null);
        await driver.DisposeAsync();

        await Assert.DoesNotThrowAsync(async () => await driver.DisposeAsync(), "the stopper and the container may both release the driver");
    }

    private EezFollower Follower(IEezL2Engine engine)
    {
        ResumePointFinder resumePoints = new(_l1, _chain, Registry, RollupId, _config.RegistryDeployBlock, _config.L1LogScanBlocks);
        return new EezFollower(_l1, _scanner, _reconciler, resumePoints, _unsafeHead, new Rollup0HeaderRules(MainnetSpecProvider.Instance, DerivedChain.Context), EezChainRules.Rollup0, _chain, engine, _config, LimboLogs.Instance);
    }

    private EezDriver Driver(IEezSequencer? sequencer, IProcessExitSource? processExit = null, ITimestamper? clock = null) =>
        new(_follower, sequencer, _config, clock ?? Timestamper.Default, processExit ?? Substitute.For<IProcessExitSource>(), LimboLogs.Instance);

    private Task<SettledHead> Settled(CallInfo call, ulong l2Block)
    {
        _cursors.Add(call.ArgAt<SettledHead>(2).Endpoint);
        return Task.FromResult(new SettledHead(L2(l2Block), Keccak.Zero));
    }

    private Task<SettledHead> Busy(CallInfo call)
    {
        _cursors.Add(call.ArgAt<SettledHead>(2).Endpoint);
        return Task.FromException<SettledHead>(new EezEngineUnavailableException("busy"));
    }

    private void SettleTo(ulong l2Block) =>
        _reconciler.Reconcile(Arg.Any<ScannedBatch>(), Arg.Any<L1Anchor>(), Arg.Any<SettledHead>(), Arg.Any<FollowerHeads>()).Returns(call => Settled(call, l2Block));

    private void GrowL1(ulong to, string fork, ulong from = 0)
    {
        for (ulong number = from; number <= to; number++)
        {
            _l1Chain[number] = Keccak.Compute($"L1 block {number} on fork {fork}");
        }
    }

    private EezL1Block L1Block(ulong number) => new() { Number = number, Hash = _l1Chain[number], MixHash = Keccak.Zero };

    private BlockHeader L2(ulong number) => _chain.FindHeader(number, BlockTreeLookupOptions.None)!;

    private L1SettlingBlock Settling(ulong number) => new(number, _l1Chain[number], [], []);

    private static ScannedBatch Batch(L1SettlingBlock block)
    {
        L1Batch batch = new(block.Number, block.Hash, Keccak.Compute("batch"), 1, true, Keccak.Zero.ValueHash256, [Keccak.OfAnEmptyString.ValueHash256], default);
        return new ScannedBatch(batch, new L1Settlement(0, 1, Keccak.OfAnEmptyString.ValueHash256, Keccak.Zero.ValueHash256));
    }

    private EezL1Log SettledRoot(ulong l1Block, ulong l2Block) => new()
    {
        Address = Registry,
        Topics = [L1BatchScanner.L2ExecutionPerformedTopic, L1BatchScanner.RollupTopic(RollupId)],
        Data = [.. L2(l2Block).Hash!.Bytes, .. new byte[32]],
        BlockNumber = l1Block,
        BlockHash = _l1Chain[l1Block],
        TransactionHash = Keccak.Compute($"tx {l1Block}"),
    };
}
