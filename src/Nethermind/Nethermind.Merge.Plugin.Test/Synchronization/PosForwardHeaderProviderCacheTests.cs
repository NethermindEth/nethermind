// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.Synchronization;

public class PosForwardHeaderProviderCacheTests
{
    private const int CachedBatchSize = 64;
    private const int Requested = 16;

    private IChainLevelHelper _chainLevelHelper = null!;
    private IPoSSwitcher _poSSwitcher = null!;
    private IBeaconPivot _beaconPivot = null!;
    private IBlockTree _blockTree = null!;
    // The pre-merge seal validator the merge decorator wraps, such as Ethash (AuRa merge chains wrap AuRa's).
    private ISealValidator _sealValidator = null!;
    private SealCallRecorder _mergeSealValidator = null!;
    private PosForwardHeaderProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _chainLevelHelper = Substitute.For<IChainLevelHelper>();
        // `ChainLevelHelper.GetStartingPoint` in the walk-back path returns the anchor at
        // `BestKnownNumber`, so `headers[0].Number == BestKnownNumber` (here `0`).
        _chainLevelHelper.GetNextHeaders(default, default, default).ReturnsForAnyArgs(_ => BuildSequentialHeaders(start: 0, count: CachedBatchSize));

        // GetBlockConsensusInfo returns (false, false) unless a test configures it, so every header is pre-merge.
        _poSSwitcher = Substitute.For<IPoSSwitcher>();
        _poSSwitcher.HasEverReachedTerminalBlock().Returns(true);

        _beaconPivot = Substitute.For<IBeaconPivot>();
        _beaconPivot.BeaconPivotExists().Returns(true);
        _beaconPivot.ProcessDestination = BuildHeader(1_000, TestItem.KeccakA);

        _sealValidator = Substitute.For<ISealValidator>();
        _sealValidator.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns(true);

        _blockTree = Substitute.For<IBlockTree>();
        _blockTree.BestKnownNumber.Returns(0UL);

        _provider = CreateProvider(_poSSwitcher);
    }

    // The provider sees the real merge decorator, behind a recorder of every seal the provider asks it to validate.
    private PosForwardHeaderProvider CreateProvider(IPoSSwitcher poSSwitcher)
    {
        _mergeSealValidator = new SealCallRecorder(new MergeSealValidator(poSSwitcher, _sealValidator));
        return new PosForwardHeaderProvider(
            _chainLevelHelper,
            poSSwitcher,
            _beaconPivot,
            _mergeSealValidator,
            _blockTree,
            Substitute.For<ISyncPeerPool>(),
            new NullSyncReport(),
            LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    [Test]
    public void Disposing_the_container_unsubscribes_the_forward_header_provider()
    {
        using (IContainer container = new ContainerBuilder()
            .AddModule(new MergeSynchronizerModule())
            .AddSingleton(_chainLevelHelper)
            .AddSingleton(Substitute.For<IPoSSwitcher>())
            .AddSingleton(_beaconPivot)
            .AddSingleton(_sealValidator)
            .AddSingleton(_blockTree)
            .AddSingleton(Substitute.For<ISyncPeerPool>())
            .AddSingleton<ISyncReport>(new NullSyncReport())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .Build())
        {
            container.Resolve<IForwardHeaderProvider>();
        }

        _blockTree.Received(1).OnUpdateMainChain -= Arg.Any<EventHandler<OnUpdateMainChainArgs>>();
    }

    private Task<IOwnedReadOnlyList<BlockHeader?>?> Get(ulong skip = 0, ulong max = Requested) =>
        _provider.GetBlockHeaders(skip, max, CancellationToken.None);

    private void RaiseMainChainUpdate(params Block[] blocks)
    {
        List<BlockHeader> headers = [with(blocks.Length)];
        foreach (Block block in blocks) headers.Add(block.Header);
        _blockTree.OnUpdateMainChain += Raise.EventWith(_blockTree, new OnUpdateMainChainArgs(headers, wereProcessed: true));
    }

    private void AssertChainLevelCalls(int expected) =>
        _chainLevelHelper.ReceivedWithAnyArgs(expected).GetNextHeaders(default, default, default);

    private async Task ExpectCalls(int expected, Action<IOwnedReadOnlyList<BlockHeader?>> between, ulong firstSkip = 0, ulong firstMax = Requested, ulong secondSkip = 0, ulong secondMax = Requested)
    {
        using IOwnedReadOnlyList<BlockHeader?>? first = await Get(firstSkip, firstMax);
        between(first!);
        using IOwnedReadOnlyList<BlockHeader?>? _ = await Get(secondSkip, secondMax);
        AssertChainLevelCalls(expected);
    }

    [Test]
    public async Task Hints_each_batch_range_before_validating_its_seals()
    {
        // This branch never reaches RequestHeaders, so the hint must come from ValidateSeals itself; without it
        // Ethash refuses every forced pre-merge PoW seal and sync from genesis stalls. Cover both entry points:
        // the freshly fetched batch and the cache-served slice. ValidateSeals runs its checks in parallel, so
        // record the sequence explicitly instead of relying on a matcher over the interleaving.
        List<string> events = [];
        Lock eventsLock = new();
        _sealValidator.When(v => v.HintValidationRange(Arg.Any<Guid>(), Arg.Any<ulong>(), Arg.Any<ulong>()))
            .Do(ci => { lock (eventsLock) events.Add($"hint {ci.ArgAt<ulong>(1)}-{ci.ArgAt<ulong>(2)}"); });
        _sealValidator.When(v => v.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()))
            .Do(_ => { lock (eventsLock) events.Add("seal"); });

        using IOwnedReadOnlyList<BlockHeader?>? fresh = await Get();
        int afterFresh = events.Count;
        using IOwnedReadOnlyList<BlockHeader?>? cached = await Get();

        string sequence = string.Join(", ", events);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(events[0], Is.EqualTo($"hint 0-{CachedBatchSize - 1}"), sequence);
            Assert.That(events.GetRange(1, afterFresh - 1), Has.All.EqualTo("seal"), sequence);
            Assert.That(events[afterFresh], Is.EqualTo($"hint 0-{Requested - 1}"), sequence);
            Assert.That(events.GetRange(afterFresh + 1, events.Count - afterFresh - 1), Has.All.EqualTo("seal"), sequence);
        }
    }

    // The fresh batch is 0..63: numbers from `firstPostMerge` up are post-merge and `terminal` is the terminal PoW
    // block (-1 for none). The merge decorator passes the seals of all pre-merge headers, the terminal block included,
    // to the pre-merge validator, so the hint must span exactly those headers, and nothing when there are none.
    // Without local terminal evidence the beacon pivot alone selects this path, as on a fresh node.
    [TestCase(0, -1, false, false, "", TestName = "Does_not_hint_post_merge_headers")]
    [TestCase(10, -1, false, true, "0-9", TestName = "Hints_only_the_pre_merge_headers_of_a_mixed_batch")]
    [TestCase(10, 9, false, true, "0-9", TestName = "Hints_the_terminal_block_before_post_merge_headers")]
    [TestCase(1, 0, false, true, "0-0", TestName = "Hints_a_terminal_block_anchor_of_a_post_merge_batch")]
    [TestCase(10, -1, true, true, "1-9", TestName = "Skips_a_null_header_when_hinting")]
    public async Task Hints_only_headers_whose_seal_reaches_the_pre_merge_validator(int firstPostMerge, int terminal, bool nullFirst, bool terminalReachedLocally, string expectedHint)
    {
        _poSSwitcher.HasEverReachedTerminalBlock().Returns(terminalReachedLocally);
        _poSSwitcher.GetBlockConsensusInfo(Arg.Any<BlockHeader>()).Returns(ci =>
        {
            int number = (int)ci.Arg<BlockHeader>().Number;
            return (number == terminal, number >= firstPostMerge);
        });
        BlockHeader[] batch = BuildSequentialHeaders(start: 0, count: CachedBatchSize);
        if (nullFirst) batch[0] = null!;

        await AssertFreshBatchHint(batch, expectedHint);
    }

    [Test]
    public async Task Hints_the_terminal_block_with_the_real_switcher()
    {
        // TTD 9_500 is crossed at block 9: blocks 0-8 are below it, block 9 is the terminal PoW block and the
        // zero-difficulty blocks after it are post-merge. No terminal block is recorded or finalized yet.
        PoSSwitcher poSSwitcher = new(new MergeConfig { TerminalTotalDifficulty = "9500" }, new SyncConfig(), new MemDb(), _blockTree, new TestSpecProvider(London.Instance), new ChainSpec(), LimboLogs.Instance);
        _provider.Dispose();
        _provider = CreateProvider(poSSwitcher);
        BlockHeader[] batch = BuildSequentialHeaders(start: 0, count: CachedBatchSize);
        UInt256 totalDifficulty = 0;
        foreach (BlockHeader header in batch)
        {
            header.Difficulty = header.Number <= 9 ? 1_000UL : 0UL;
            totalDifficulty += header.Difficulty;
            header.TotalDifficulty = totalDifficulty;
        }

        await AssertFreshBatchHint(batch, "0-9");
    }

    private async Task AssertFreshBatchHint(BlockHeader[] batch, string expectedHint)
    {
        _chainLevelHelper.GetNextHeaders(default, default, default).ReturnsForAnyArgs(batch);
        List<string> hints = [];
        ConcurrentBag<ulong> preMergeSeals = [];
        _sealValidator.When(v => v.HintValidationRange(Arg.Any<Guid>(), Arg.Any<ulong>(), Arg.Any<ulong>()))
            .Do(ci => hints.Add($"{ci.ArgAt<ulong>(1)}-{ci.ArgAt<ulong>(2)}"));
        _sealValidator.When(v => v.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()))
            .Do(ci => preMergeSeals.Add(ci.Arg<BlockHeader>().Number));

        using IOwnedReadOnlyList<BlockHeader?>? _ = await Get();

        string preMergeRange = preMergeSeals.IsEmpty ? "" : $"{preMergeSeals.Min()}-{preMergeSeals.Max()}";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(string.Join(", ", hints), Is.EqualTo(expectedHint));
            Assert.That(string.Join(", ", hints), Is.EqualTo(preMergeRange), "the hint must match the seals the pre-merge validator checks");
            Assert.That(_mergeSealValidator.Seals, Is.EquivalentTo(batch.Where(h => h is not null).Select(h => h.Number)), "every header must still have its seal validated, once");
        }
    }

    [Test]
    public Task Second_call_with_same_inputs_is_served_from_cache() =>
        ExpectCalls(expected: 1, between: _ => { });

    [Test]
    public Task Cache_is_invalidated_when_process_destination_hash_changes() =>
        ExpectCalls(expected: 2, between: _ =>
            _beaconPivot.ProcessDestination = BuildHeader(1_000, TestItem.KeccakB));

    [Test]
    public Task Cache_miss_when_best_known_number_advances_past_cached_range() =>
        ExpectCalls(expected: 2, between: _ =>
            _blockTree.BestKnownNumber.Returns((ulong)CachedBatchSize + 10));

    [Test]
    public Task Cache_is_invalidated_when_skipLastN_changes() =>
        ExpectCalls(expected: 2, between: _ => { }, secondSkip: 4);

    [Test]
    public Task Cache_is_invalidated_on_reorg_within_cached_range() =>
        ExpectCalls(expected: 2, between: first =>
        {
            Block reorgBlock = Build.A.Block.WithNumber(10).WithDifficulty(2).TestObject;
            Assert.That(reorgBlock.Header.Hash, Is.Not.EqualTo(first[10]!.Hash),
                "precondition: reorg block must hash differently from the cached header at the same height");
            RaiseMainChainUpdate(reorgBlock);
        });

    [Test]
    public async Task Cache_is_invalidated_on_ascending_reorg_starting_below_cached_range()
    {
        const ulong cacheStart = 100;
        _chainLevelHelper.GetNextHeaders(default, default, default).ReturnsForAnyArgs(_ => BuildSequentialHeaders(start: cacheStart, count: CachedBatchSize));
        _blockTree.BestKnownNumber.Returns(cacheStart);

        using IOwnedReadOnlyList<BlockHeader?>? first = await Get();

        Block belowBlock = Build.A.Block.WithNumber(cacheStart - 5).WithDifficulty(2).TestObject;
        Block insideBlock = Build.A.Block.WithNumber(cacheStart + 3).WithDifficulty(2).TestObject;
        Assert.That(insideBlock.Header.Hash, Is.Not.EqualTo(first![3]!.Hash));
        RaiseMainChainUpdate(belowBlock, insideBlock);

        using IOwnedReadOnlyList<BlockHeader?>? _ = await Get();
        AssertChainLevelCalls(2);
    }

    [Test]
    public Task Cache_is_invalidated_on_descending_reorg_with_top_above_cache() =>
        ExpectCalls(expected: 2, between: _ =>
        {
            Block above = Build.A.Block.WithNumber(CachedBatchSize + 50).WithDifficulty(2).TestObject;
            Block inside = Build.A.Block.WithNumber(10).WithDifficulty(2).TestObject;
            RaiseMainChainUpdate(above, inside);
        });

    [Test]
    public Task Cache_survives_main_chain_update_that_matches_cached_hash() =>
        ExpectCalls(expected: 1, between: first =>
            RaiseMainChainUpdate(new Block(first[5]!, new BlockBody())));

    [Test]
    public async Task Cached_slice_advances_with_best_known_number()
    {
        using IOwnedReadOnlyList<BlockHeader?>? first = await Get();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first!.Count, Is.EqualTo(Requested));
            Assert.That(first[0]!.Number, Is.EqualTo(0));
        }

        _blockTree.BestKnownNumber.Returns(20UL);
        using IOwnedReadOnlyList<BlockHeader?>? second = await Get();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second!.Count, Is.EqualTo(Requested));
            Assert.That(second[0]!.Number, Is.EqualTo(20));
        }
        AssertChainLevelCalls(1);
    }

    private static BlockHeader[] BuildSequentialHeaders(ulong start, int count)
    {
        BlockHeader[] headers = new BlockHeader[count];
        BlockHeader? parent = null;
        for (int i = 0; i < count; i++)
        {
            BlockHeaderBuilder builder = Build.A.BlockHeader.WithNumber(start + (ulong)i);
            if (parent is not null) builder = builder.WithParent(parent);
            headers[i] = builder.TestObject;
            parent = headers[i];
        }
        return headers;
    }

    private static BlockHeader BuildHeader(ulong number, Hash256 hash) =>
        Build.A.BlockHeader.WithNumber(number).WithHash(hash).TestObject;

    private sealed class SealCallRecorder(ISealValidator inner) : ISealValidator
    {
        public ConcurrentBag<ulong> Seals { get; } = [];

        public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle) => inner.ValidateParams(parent, header, isUncle);

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            Seals.Add(header.Number);
            return inner.ValidateSeal(header, force);
        }

        public void HintValidationRange(Guid guid, ulong start, ulong end) => inner.HintValidationRange(guid, start, end);
    }
}
