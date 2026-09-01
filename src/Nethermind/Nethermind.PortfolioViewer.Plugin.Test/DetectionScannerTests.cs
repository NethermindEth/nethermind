// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Find;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.PortfolioViewer.Plugin.Test;

[TestFixture]
public class DetectionScannerTests
{
    private const long ChainId = 100;
    private static readonly Hash256 Transfer = new("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef");
    private static readonly Address Account = new("0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045");
    private static readonly Address Token = new("0x1111111111111111111111111111111111111111");
    private static readonly Address Nft = new("0x2222222222222222222222222222222222222222");

    private string _dir = null!;
    private DetectionCache _cache = null!;
    private ILogFinder _logFinder = null!;
    private IBlockFinder _blockFinder = null!;
    private CapturingScheduler _scheduler = null!;
    private DetectionScanner _scanner = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bv-scanner-" + TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(_dir);
        _cache = new DetectionCache(_dir, LimboLogs.Instance);
        _logFinder = Substitute.For<ILogFinder>();
        _blockFinder = Substitute.For<IBlockFinder>();
        _scheduler = new CapturingScheduler();
        _scanner = new DetectionScanner(_scheduler, _logFinder, _blockFinder, _cache, LimboLogs.Instance, (ulong)ChainId);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static FilterLog Log(Address address, params Hash256[] topics) =>
        new(0, 5, 0, Keccak.Zero, 0, Keccak.Zero, address, [], topics);

    // seed an incomplete entry carrying the head, so the scan resumes from it without needing IBlockFinder
    private void SeedHead(long head) =>
        _cache.Put(ChainId, Account.ToString(), new DetectionEntry([], [], head + 1, head, false, 0));

    [Test]
    public async Task Discovers_erc20_contracts_completes_and_skips_nfts()
    {
        SeedHead(5); // one 5k-block chunk covers [0..5] -> completes in a single pass
        _logFinder.FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>())
            .Returns([Log(Token, Transfer, Keccak.Zero, Keccak.Zero), Log(Nft, Transfer, Keccak.Zero, Keccak.Zero, Keccak.Zero)]);

        _scanner.RequestScan(ChainId, Account);
        await _scheduler.RunAll();

        DetectionEntry? entry = _cache.Get(ChainId, Account.ToString());
        Assert.That(entry, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry!.Complete, Is.True);
            Assert.That(entry.ScannedFrom, Is.EqualTo(0));
            Assert.That(entry.Contracts, Is.EquivalentTo(new[] { Token.ToString() }), "ERC-20 kept, ERC-721 (4 topics) dropped");
        }
    }

    [Test]
    public async Task Discovers_nft_collections()
    {
        SeedHead(5);
        // a 4-topic Transfer (ERC-721) log — should land in NftContracts, not Contracts
        _logFinder.FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>())
            .Returns([Log(Nft, Transfer, Keccak.Zero, Keccak.Zero, Keccak.Zero)]);

        _scanner.RequestScan(ChainId, Account);
        await _scheduler.RunAll();

        DetectionEntry? entry = _cache.Get(ChainId, Account.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry!.Complete, Is.True);
            Assert.That(entry.NftContracts, Does.Contain(Nft.ToString()));
            Assert.That(entry.Contracts, Is.Empty);
        }
    }

    [Test]
    public void No_op_when_already_complete()
    {
        _cache.Put(ChainId, Account.ToString(), new DetectionEntry([Token.ToString()], [], 0, 5, true, 0));

        _scanner.RequestScan(ChainId, Account);

        Assert.That(_scheduler.Count, Is.Zero, "nothing scheduled");
        _logFinder.DidNotReceive().FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Forward_gap_is_rescanned_when_head_advances()
    {
        // previously scanned fully (down to genesis) up to block 5
        _cache.Put(ChainId, Account.ToString(), new DetectionEntry([Token.ToString()], [], 0, 5, true, 0));
        // the chain has since advanced and a new ERC-20 transfer arrived in the gap
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(100).TestObject);
        Address newToken = new("0x3333333333333333333333333333333333333333");
        _logFinder.FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>())
            .Returns([Log(newToken, Transfer, Keccak.Zero, Keccak.Zero)]);

        _scanner.RequestScan(ChainId, Account);
        await _scheduler.RunAll();

        DetectionEntry? entry = _cache.Get(ChainId, Account.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry!.Head, Is.EqualTo(100), "covered range extended to the new head");
            Assert.That(entry.Complete, Is.True, "still complete downward");
            Assert.That(entry.Contracts, Does.Contain(newToken.ToString()), "token received since the last scan is detected");
            Assert.That(entry.Contracts, Does.Contain(Token.ToString()), "previously detected contracts retained");
        }
    }

    [Test]
    public async Task Forward_phase_keeps_partial_results_when_cancelled()
    {
        // fully scanned down to genesis up to block 5; the chain has since advanced, so the forward phase runs
        _cache.Put(ChainId, Account.ToString(), new DetectionEntry([Token.ToString()], [], 0, 5, true, 0));
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(100).TestObject);
        Address newToken = new("0x3333333333333333333333333333333333333333");
        // the first topic scan finds a new token, then a later scan in the same chunk is pre-empted
        _logFinder.FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IEnumerable<FilterLog>)[Log(newToken, Transfer, Keccak.Zero, Keccak.Zero)],
                     _ => throw new OperationCanceledException());

        _scanner.RequestScan(ChainId, Account);
        await _scheduler.RunNext(); // forward chunk pre-empted after finding newToken

        DetectionEntry? entry = _cache.Get(ChainId, Account.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry!.Head, Is.EqualTo(5), "upper bound not advanced on cancellation");
            Assert.That(entry.Complete, Is.True);
            Assert.That(entry.Contracts, Does.Contain(newToken.ToString()), "contract found before pre-emption is persisted, not discarded");
            Assert.That(entry.Contracts, Does.Contain(Token.ToString()), "previously detected contract retained");
            Assert.That(_scheduler.Count, Is.EqualTo(1), "the forward chunk was rescheduled to retry");
        }
    }

    [Test]
    public void Ignores_scan_for_a_foreign_chain_id()
    {
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(5).TestObject);

        _scanner.RequestScan(ChainId + 1, Account); // scanner only serves its local chain (ChainId)

        Assert.That(_scheduler.Count, Is.Zero, "no scan scheduled for a non-local chain id");
        _logFinder.DidNotReceive().FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Forward_phase_rescans_a_reorg_overlap_below_the_last_head()
    {
        // fully covered up to block 1000; the chain advanced by one block
        _cache.Put(ChainId, Account.ToString(), new DetectionEntry([], [], 0, 1000, true, 0));
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(1001).TestObject);
        long fromBlock = -1;
        _logFinder.FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>())
            .Returns(ci => { fromBlock = (long)(ci.Arg<LogFilter>().FromBlock.BlockNumber ?? 0); return (IEnumerable<FilterLog>)[]; });

        _scanner.RequestScan(ChainId, Account);
        await _scheduler.RunNext();

        Assert.That(fromBlock, Is.LessThanOrEqualTo(1000), "forward pass re-scans blocks at/below the old head so a shallow reorg is caught");
    }

    [Test]
    public async Task Forward_phase_below_retained_history_advances_to_head()
    {
        // covered up to block 5, but the node was offline and blocks above 5 are now below the receipts floor
        _cache.Put(ChainId, Account.ToString(), new DetectionEntry([], [], 0, 5, true, 0));
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(1_000_000).TestObject);
        _logFinder.FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new ResourceNotFoundException("receipts unavailable"));

        _scanner.RequestScan(ChainId, Account);
        await _scheduler.RunNext();

        DetectionEntry? entry = _cache.Get(ChainId, Account.ToString());
        Assert.That(entry!.Head, Is.EqualTo(1_000_000), "covered head advances past the unavailable gap instead of retrying it forever");
    }

    [Test]
    public void No_op_when_complete_and_head_unchanged()
    {
        _cache.Put(ChainId, Account.ToString(), new DetectionEntry([Token.ToString()], [], 0, 100, true, 0));
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(100).TestObject);

        _scanner.RequestScan(ChainId, Account);

        Assert.That(_scheduler.Count, Is.Zero, "nothing scheduled when fully covered and no new blocks");
    }

    [Test]
    public async Task Cancelled_chunk_is_retried_without_advancing()
    {
        SeedHead(20_000); // more than one chunk, so a cancel leaves work outstanding
        _logFinder.FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new OperationCanceledException());

        _scanner.RequestScan(ChainId, Account);
        await _scheduler.RunNext(); // one chunk, pre-empted mid-scan

        DetectionEntry? entry = _cache.Get(ChainId, Account.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry!.Complete, Is.False, "not marked complete on cancellation");
            Assert.That(entry.ScannedFrom, Is.EqualTo(20_001), "cursor not advanced (range will be retried)");
            Assert.That(_scheduler.Count, Is.EqualTo(1), "the chunk was rescheduled to retry later");
        }
    }

    [Test]
    public async Task Chunk_shrinks_after_cancellation()
    {
        SeedHead(100_000); // large enough that the first chunk is the base size
        List<long> widths = [];
        bool firstCancels = true;
        _logFinder.FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                LogFilter f = ci.Arg<LogFilter>();
                widths.Add((long)(f.ToBlock.BlockNumber ?? 0) - (long)(f.FromBlock.BlockNumber ?? 0));
                if (firstCancels) { firstCancels = false; throw new OperationCanceledException(); }
                return (IEnumerable<FilterLog>)[];
            });

        _scanner.RequestScan(ChainId, Account);
        await _scheduler.RunNext(); // first chunk pre-empted -> shrink, retry rescheduled
        await _scheduler.RunNext(); // retry runs at the smaller size

        using (Assert.EnterMultipleScope())
        {
            Assert.That(widths[0], Is.EqualTo(4_999), "first chunk uses the base size (5000 blocks)");
            Assert.That(widths[^1], Is.LessThan(widths[0]), "chunk shrank after the cancellation");
        }
    }

    [Test]
    public async Task Reaching_pruned_history_marks_complete()
    {
        SeedHead(5);
        // LogFinder throws ResourceNotFoundException once it walks below the retained receipts
        _logFinder.FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new ResourceNotFoundException("receipts unavailable"));

        _scanner.RequestScan(ChainId, Account);
        await _scheduler.RunAll();

        DetectionEntry? entry = _cache.Get(ChainId, Account.ToString());
        Assert.That(entry!.Complete, Is.True, "hitting the retained-history floor completes the scan");
    }

    [Test]
    public async Task Downward_walk_clamps_to_the_retained_history_floor_and_completes_there()
    {
        SeedHead(100);
        _blockFinder.GetLowestBlock().Returns(50UL);
        List<long> fromBlocks = [];
        _logFinder.FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>())
            .Returns(ci => { fromBlocks.Add((long)(ci.Arg<LogFilter>().FromBlock.BlockNumber ?? 0)); return (IEnumerable<FilterLog>)[]; });

        _scanner.RequestScan(ChainId, Account);
        await _scheduler.RunAll();

        DetectionEntry? entry = _cache.Get(ChainId, Account.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromBlocks, Has.All.GreaterThanOrEqualTo(50));
            Assert.That(fromBlocks, Does.Contain(50));
            Assert.That(entry!.Complete, Is.True, "reaching the floor completes the scan in one pass");
            Assert.That(entry.ScannedFrom, Is.EqualTo(0));
        }
    }

    [Test]
    public async Task Forward_walk_clamps_to_the_retained_history_floor_instead_of_skipping_the_gap()
    {
        _cache.Put(ChainId, Account.ToString(), new DetectionEntry([], [], 0, 5, true, 0));
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(100_000).TestObject);
        _blockFinder.GetLowestBlock().Returns(50UL);
        List<long> fromBlocks = [];
        _logFinder.FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>())
            .Returns(ci => { fromBlocks.Add((long)(ci.Arg<LogFilter>().FromBlock.BlockNumber ?? 0)); return (IEnumerable<FilterLog>)[]; });

        _scanner.RequestScan(ChainId, Account);
        await _scheduler.RunAll();

        DetectionEntry? entry = _cache.Get(ChainId, Account.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromBlocks, Has.All.GreaterThanOrEqualTo(50), "no chunk asks below the floor, so the fail-closed guard is never tripped");
            Assert.That(fromBlocks, Does.Contain(50), "the recoverable [floor, head] portion is scanned, not skipped");
            Assert.That(entry!.Head, Is.EqualTo(100_000));
        }
    }

    [Test]
    public async Task Forward_walk_with_the_floor_above_the_head_resumes_from_the_head_without_querying()
    {
        _cache.Put(ChainId, Account.ToString(), new DetectionEntry([Token.ToString()], [], 0, 5, true, 0));
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(100).TestObject);
        _blockFinder.GetLowestBlock().Returns(500UL);

        _scanner.RequestScan(ChainId, Account);
        await _scheduler.RunAll();

        DetectionEntry? entry = _cache.Get(ChainId, Account.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry!.Head, Is.EqualTo(100), "coverage resumes from the current head");
            Assert.That(entry.Contracts, Does.Contain(Token.ToString()), "previously detected contracts retained");
        }
        _logFinder.DidNotReceive().FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Scan_already_below_the_floor_completes_without_querying()
    {
        _cache.Put(ChainId, Account.ToString(), new DetectionEntry([Token.ToString()], [], 20, 100, false, 0));
        _blockFinder.GetLowestBlock().Returns(50UL);

        _scanner.RequestScan(ChainId, Account);
        await _scheduler.RunAll();

        DetectionEntry? entry = _cache.Get(ChainId, Account.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry!.Complete, Is.True);
            Assert.That(entry.ScannedFrom, Is.EqualTo(0));
            Assert.That(entry.Contracts, Does.Contain(Token.ToString()), "previously detected contracts retained");
        }
        _logFinder.DidNotReceive().FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Fresh_scan_reads_head_from_block_finder()
    {
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(5).TestObject);
        _logFinder.FindLogs(Arg.Any<LogFilter>(), Arg.Any<CancellationToken>())
            .Returns([Log(Token, Transfer, Keccak.Zero, Keccak.Zero)]);

        _scanner.RequestScan(ChainId, Account);
        await _scheduler.RunAll();

        DetectionEntry? entry = _cache.Get(ChainId, Account.ToString());
        Assert.That(entry, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry!.Complete, Is.True);
            Assert.That(entry.Contracts, Does.Contain(Token.ToString()));
        }
    }

    // Captures scheduled tasks instead of running them, so tests drive execution deterministically
    // (running a completed chunk enqueues the next; a cancelled/rescheduled chunk re-enqueues itself).
    private sealed class CapturingScheduler : IBackgroundTaskScheduler
    {
        private readonly Queue<Func<CancellationToken, Task>> _queue = new();

        public int Count => _queue.Count;
        public bool Full { get; set; }

        public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null, string? source = null)
        {
            if (Full) return false;
            _queue.Enqueue(ct => fulfillFunc(request, ct));
            return true;
        }

        public async Task RunNext(CancellationToken token = default)
        {
            if (_queue.Count > 0) await _queue.Dequeue()(token);
        }

        public async Task RunAll(CancellationToken token = default)
        {
            int guard = 0;
            while (_queue.Count > 0 && guard++ < 10_000) await _queue.Dequeue()(token);
        }
    }
}
