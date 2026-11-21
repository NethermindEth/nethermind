// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Config;
using Nethermind.Logging;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class ResumePointFinderTests
{
    private const ulong RollupId = 1;
    private const ulong DeployBlock = 3;
    private static readonly Address Registry = new("0x5fbdb2315678afecb367f032d93f642f64180aa3");

    private IEezL1Api _l1 = null!;
    private IBlockTree _l2 = null!;

    [SetUp]
    public void SetUp()
    {
        _l1 = Substitute.For<IEezL1Api>();
        _l2 = Build.A.BlockTree().OfChainLength(10).TestObject;
        for (ulong number = DeployBlock; number <= 100; number++)
        {
            _l1.GetBlockByNumber(number, Arg.Any<CancellationToken>()).Returns(new EezL1Block { Number = number, Hash = L1Hash(number), MixHash = Keccak.Zero });
        }
    }

    /// <summary>
    /// Two batches settle our rollup in L1 block 40, ending at L2 blocks 5 and then 7:
    /// the block's final commitment is 7, so the follower resumes there, after block 40.
    /// </summary>
    [Test]
    public async Task Find_BlockWithTwoSettlements_ResumesAtItsLastOne()
    {
        Settlements(Settled(40, 1, 0, 5), Settled(40, 3, 0, 7), Settled(30, 1, 0, 2));

        SettledRecord? record = await Finder(scanBlocks: 100).Find(100, CancellationToken.None);

        Assert.That((record?.L1Number, record?.L2End.Number), Is.EqualTo(((ulong?)40, (ulong?)7)), "the commitment L1 stores after block 40");
    }

    [Test]
    public async Task Find_LatestSettlementNotLocal_FallsBackToTheLastLocalOne()
    {
        Settlements(Settled(50, 0, 0, Keccak.Compute("a block this node never derived")), Settled(30, 1, 0, 4));

        SettledRecord? record = await Finder(scanBlocks: 100).Find(100, CancellationToken.None);

        Assert.That(record?.L2End.Number, Is.EqualTo(4), "a settlement of blocks the local chain does not hold cannot be resumed from");
    }

    [Test]
    public async Task Find_SettlementOnAReorganizedL1Block_IsSkipped()
    {
        EezL1Log orphaned = Settled(50, 0, 0, 8) with { BlockHash = Keccak.Compute("orphaned L1 block 50") };
        Settlements(orphaned, Settled(30, 1, 0, 4));

        SettledRecord? record = await Finder(scanBlocks: 100).Find(100, CancellationToken.None);

        Assert.That(record?.L1Number, Is.EqualTo(30), "a settlement L1 reorganized away no longer counts");
    }

    [Test]
    public async Task Find_OlderSettlementBeyondOneChunk_KeepsWalkingBack()
    {
        _l1.GetLogs(Registry, L1BatchScanner.L2ExecutionPerformedTopic, L1BatchScanner.RollupTopic(RollupId), Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<ulong>(3) <= 12 && call.ArgAt<ulong>(4) >= 12 ? new[] { Settled(12, 0, 0, 3) } : []);

        SettledRecord? record = await Finder(scanBlocks: 10).Find(100, CancellationToken.None);

        Assert.That(record?.L1Number, Is.EqualTo(12), "the search goes back chunk by chunk to the deploy block");
    }

    [Test]
    public async Task Find_NodeRefusesTheChunk_HalvesUntilItServesIt()
    {
        _l1.GetLogs(Registry, L1BatchScanner.L2ExecutionPerformedTopic, L1BatchScanner.RollupTopic(RollupId), Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<ulong>(4) - call.ArgAt<ulong>(3) >= 8 ? null
                : call.ArgAt<ulong>(3) <= 40 && call.ArgAt<ulong>(4) >= 40 ? new[] { Settled(40, 0, 0, 6) } : []);

        SettledRecord? record = await Finder(scanBlocks: 100).Find(100, CancellationToken.None);

        Assert.That(record?.L2End.Number, Is.EqualTo(6), "a node that caps the log range still serves the search in smaller pieces");
    }

    [Test]
    public async Task Find_NothingSettled_ReturnsNull()
    {
        Settlements();

        Assert.That(await Finder(scanBlocks: 100).Find(100, CancellationToken.None), Is.Null, "with no settlement the follower starts from genesis");
    }

    [TestCase(false, TestName = "CatchUpAfterLiveKeepsAcceptedSeed")]
    [TestCase(true, TestName = "ReorganizedLiveDoesNotRefreshSeed")]
    public async Task Find_CatchUpEndpoint_RecoversCanonicalLiveSeedWithoutRetreating(bool reorganized)
    {
        RollupXSchedule schedule = new(1, 2, new RollupXL1Reference(10200, 3, L1Hash(3), 1000, TestItem.KeccakA), 1000);
        for (ulong number = DeployBlock; number <= 100; number++)
        {
            _l1.GetBlockByNumber(number, Arg.Any<CancellationToken>()).Returns(new EezL1Block
            {
                Number = number,
                Hash = L1Hash(number),
                Timestamp = 1000 + number - DeployBlock,
                MixHash = TestItem.KeccakB,
            });
        }
        BlockHeader endpoint = _l2.FindHeader(4)!;
        endpoint.MixHash = TestItem.KeccakA;
        EezL1Log live = Settled(7, 0, 0, 4);
        if (reorganized)
        {
            live = live with { BlockHash = TestItem.KeccakC };
        }
        EezL1Log catchUp = Settled(9, 0, 0, 4);
        _l1.GetLogs(Registry, L1BatchScanner.L2ExecutionPerformedTopic, L1BatchScanner.RollupTopic(RollupId), Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<ulong>(3) == 7 && call.ArgAt<ulong>(4) == 7 ? reorganized ? [] : [live] : [catchUp]);

        SettledRecord? record = await new ResumePointFinder(_l1, _l2, Registry, RollupId, DeployBlock, 100, schedule).Find(100, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(record?.L2End, Is.SameAs(endpoint), "the held catch-up endpoint remains the resume point even when finalized");
            Assert.That(record?.L1Number, Is.EqualTo(9));
            Assert.That(record?.DescendantSeed, Is.EqualTo(reorganized ? TestItem.KeccakA : TestItem.KeccakB));
        }
        _l2.ForkChoiceUpdated(endpoint.Hash, endpoint.Hash);
        _l1.GetChainId(Arg.Any<CancellationToken>()).Returns(10200UL);
        _l1.GetBlockByNumber(3, Arg.Any<CancellationToken>()).Returns(new EezL1Block
        {
            Number = 3,
            Hash = L1Hash(3),
            Timestamp = 1000,
            MixHash = TestItem.KeccakA,
        });
        _l1.GetLatestBlock(Arg.Any<CancellationToken>()).Returns(new EezL1Block { Number = 9, Hash = L1Hash(9), Timestamp = 1006, MixHash = TestItem.KeccakC });
        _l1.GetBlockByNumber(10, Arg.Any<CancellationToken>()).Returns(new EezL1Block { Number = 10, Hash = L1Hash(10), Timestamp = 1007, MixHash = TestItem.KeccakC });
        _l1.GetLatestBlock(Arg.Any<CancellationToken>()).Returns(new EezL1Block { Number = 10, Hash = L1Hash(10), Timestamp = 1007, MixHash = TestItem.KeccakC });
        IL1BatchScanner scanner = Substitute.For<IL1BatchScanner>();
        L1SettlingBlock next = new(10, L1Hash(10), [], []);
        scanner.FindSettlingBlocks(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Returns([next]);
        L1Batch batch = new(10, L1Hash(10), TestItem.KeccakD, 1, true, endpoint.Hash!, [endpoint.Hash!], default);
        scanner.Scan(Arg.Any<L1SettlingBlock>(), Arg.Any<EezL1Block>(), Arg.Any<CancellationToken>())
            .Returns([new ScannedBatch(batch, new L1Settlement(0, 1, endpoint.Hash!, endpoint.Hash!))]);
        IBatchReconciler reconciler = Substitute.For<IBatchReconciler>();
        Hash256? seenSeed = null;
        reconciler.Reconcile(Arg.Any<ScannedBatch>(), Arg.Any<L1Anchor>(), Arg.Any<SettledHead>(), Arg.Any<FollowerHeads>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            SettledHead cursor = call.ArgAt<SettledHead>(2);
            seenSeed = cursor.Seed;
            return Task.FromResult(cursor);
        });
        EezChainRules rules = EezChainRules.RollupX(schedule);
        EezConfig config = new() { L1ChainId = 10200, RegistryDeployBlock = DeployBlock, L1LogScanBlocks = 100 };
        EezFollower follower = new(_l1, scanner, reconciler, new ResumePointFinder(_l1, _l2, Registry, RollupId, DeployBlock, 100, schedule),
            NullUnsafeHeadSource.Instance, new RollupXHeaderRules(MainnetSpecProvider.Instance, rules), rules, _l2, new ChainEngine(_l2), config, LimboLogs.Instance);
        await follower.Boot(CancellationToken.None);
        await follower.Poll(CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(follower.Heads.Finalized.Hash, Is.EqualTo(endpoint.Hash), "restart preserves the finalized catch-up endpoint");
            Assert.That(seenSeed, Is.EqualTo(reorganized ? TestItem.KeccakA : TestItem.KeccakB), "the follower forwards the recovered seed into the next reconciliation");
        }
    }

    private ResumePointFinder Finder(ulong scanBlocks) => new(_l1, _l2, Registry, RollupId, DeployBlock, scanBlocks);

    private void Settlements(params EezL1Log[] logs) =>
        _l1.GetLogs(Registry, L1BatchScanner.L2ExecutionPerformedTopic, L1BatchScanner.RollupTopic(RollupId), Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Returns(logs);

    private EezL1Log Settled(ulong l1Block, ulong transactionIndex, ulong logIndex, ulong l2Block) =>
        Settled(l1Block, transactionIndex, logIndex, _l2.FindHeader(l2Block)!.Hash!);

    private static EezL1Log Settled(ulong l1Block, ulong transactionIndex, ulong logIndex, Hash256 root) => new()
    {
        Address = Registry,
        Topics = [L1BatchScanner.L2ExecutionPerformedTopic, L1BatchScanner.RollupTopic(RollupId)],
        Data = [.. root.Bytes, .. new byte[32]],
        BlockNumber = l1Block,
        BlockHash = L1Hash(l1Block),
        TransactionHash = Keccak.Compute($"tx {l1Block} {transactionIndex}"),
        TransactionIndex = transactionIndex,
        LogIndex = logIndex,
    };

    private static Hash256 L1Hash(ulong number) => Keccak.Compute($"L1 block {number}");
}
