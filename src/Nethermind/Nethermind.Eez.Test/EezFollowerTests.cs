// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Nethermind.Eez.Follower;
using Nethermind.Eez.Sequencer;
using Nethermind.Logging;
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
        _chain = Build.A.BlockTree().OfChainLength(6).TestObject;
        _engine = new ChainEngine(_chain);
        _config = new EezConfig { L1ChainId = L1ChainId, RegistryAddress = Registry.ToString(), RegistryDeployBlock = 1, RollupId = RollupId, L1LogScanBlocks = 100 };
        _l1 = Substitute.For<IEezL1Api>();
        _l1.GetChainId(Arg.Any<CancellationToken>()).Returns(L1ChainId);
        _l1.GetBlockByNumber(Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(call => _l1Chain.TryGetValue(call.ArgAt<ulong>(0), out Hash256? hash) ? new EezL1Block { Number = call.ArgAt<ulong>(0), Hash = hash } : null);
        _l1.GetLatestBlock(Arg.Any<CancellationToken>()).Returns(_ => L1Block(_l1Chain.Keys.Max()));
        GrowL1(to: 5, fork: "a");

        _scanner = Substitute.For<IL1BatchScanner>();
        _scanner.FindSettlingBlocks(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<ulong>(0) <= SettlingL1Block && SettlingL1Block <= call.ArgAt<ulong>(1) ? [Settling(SettlingL1Block)] : []);
        _scanner.Scan(Arg.Any<L1SettlingBlock>(), Arg.Any<CancellationToken>()).Returns(call => new[] { Batch(call.ArgAt<L1SettlingBlock>(0)) });
        _reconciler = Substitute.For<IBatchReconciler>();
        SettleTo(2);

        _unsafeHead = Substitute.For<IUnsafeHeadSource>();
        ResumePointFinder resumePoints = new(_l1, _chain, Registry, RollupId, _config.RegistryDeployBlock, _config.L1LogScanBlocks);
        _follower = new EezFollower(_l1, _scanner, _reconciler, resumePoints, _unsafeHead, _chain, _engine, _config, LimboLogs.Instance);
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
        _scanner.Scan(Arg.Any<L1SettlingBlock>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { Batch(call.ArgAt<L1SettlingBlock>(0)), Batch(call.ArgAt<L1SettlingBlock>(0)) });
        _reconciler.Reconcile(Arg.Any<ScannedBatch>(), Arg.Any<BlockHeader>(), Arg.Any<FollowerHeads>())
            .Returns(call => Settled(call, 1), call => Busy(call), call => Settled(call, 1), call => Settled(call, 2));
        await _follower.Boot(CancellationToken.None);

        bool behind = await _follower.Poll(CancellationToken.None);
        await _follower.Poll(CancellationToken.None);

        Assert.That(behind, Is.False, "a failed poll waits before the next one");
        Assert.That(_cursors.Select(static c => c.Number), Is.EqualTo(new ulong[] { 0, 1, 0, 1 }), "the retried block starts over from the cursor before it");
        Assert.That(_follower.Heads.Safe.Number, Is.EqualTo(2), "the retry settles the block");
    }

    [Test]
    public async Task Poll_DivergenceOnABlockL1Reorganized_IsRetried()
    {
        _reconciler.Reconcile(Arg.Any<ScannedBatch>(), Arg.Any<BlockHeader>(), Arg.Any<FollowerHeads>()).Returns(_ =>
        {
            GrowL1(to: 5, fork: "b", from: SettlingL1Block);
            return Task.FromException<BlockHeader>(new EezFollowerException("diverges"));
        });
        await _follower.Boot(CancellationToken.None);

        Assert.DoesNotThrowAsync(() => _follower.Poll(CancellationToken.None), "a batch L1 reorganized away while it was derived is not a divergence");
    }

    [Test]
    public async Task Poll_DivergenceOnACanonicalBlock_Throws()
    {
        _reconciler.Reconcile(Arg.Any<ScannedBatch>(), Arg.Any<BlockHeader>(), Arg.Any<FollowerHeads>())
            .Returns(Task.FromException<BlockHeader>(new EezFollowerException("diverges")));
        await _follower.Boot(CancellationToken.None);

        Assert.ThrowsAsync<EezFollowerException>(() => _follower.Poll(CancellationToken.None), "a settlement L1 still holds that the chain cannot reproduce stops the follower");
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

        bool behind = await _follower.Poll(CancellationToken.None);

        Assert.That(behind, Is.True, "a follower catching up polls again without waiting");
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
    public void Boot_L1OnAnotherChain_Throws() =>
        Assert.ThrowsAsync<EezFollowerException>(() =>
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
    public async Task Step_FollowerBehindL1_HoldsTheSequencer()
    {
        IEezSequencer sequencer = Substitute.For<IEezSequencer>();
        _config.L1LogScanBlocks = 2;
        await using EezDriver driver = Driver(sequencer);
        await _follower.Boot(CancellationToken.None);

        await driver.Step(default, CancellationToken.None);

        Assert.That(sequencer.ReceivedCalls(), Is.Empty, "a batch posted from a cursor L1 already moved past would revert");
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

        Assert.DoesNotThrowAsync(async () => await driver.DisposeAsync(), "the stopper and the container may both release the driver");
    }

    private EezDriver Driver(IEezSequencer? sequencer, IProcessExitSource? processExit = null) =>
        new(_follower, sequencer, _config, Timestamper.Default, processExit ?? Substitute.For<IProcessExitSource>(), LimboLogs.Instance);

    private Task<BlockHeader> Settled(CallInfo call, ulong l2Block)
    {
        _cursors.Add(call.ArgAt<BlockHeader>(1));
        return Task.FromResult(L2(l2Block));
    }

    private Task<BlockHeader> Busy(CallInfo call)
    {
        _cursors.Add(call.ArgAt<BlockHeader>(1));
        return Task.FromException<BlockHeader>(new EezEngineUnavailableException("busy"));
    }

    private void SettleTo(ulong l2Block) =>
        _reconciler.Reconcile(Arg.Any<ScannedBatch>(), Arg.Any<BlockHeader>(), Arg.Any<FollowerHeads>()).Returns(call => Settled(call, l2Block));

    private void GrowL1(ulong to, string fork, ulong from = 0)
    {
        for (ulong number = from; number <= to; number++)
        {
            _l1Chain[number] = Keccak.Compute($"L1 block {number} on fork {fork}");
        }
    }

    private EezL1Block L1Block(ulong number) => new() { Number = number, Hash = _l1Chain[number] };

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
        Data = L2(l2Block).Hash!.BytesToArray(),
        BlockNumber = l1Block,
        BlockHash = _l1Chain[l1Block],
        TransactionHash = Keccak.Compute($"tx {l1Block}"),
    };
}
