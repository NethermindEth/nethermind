// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Synchronization;
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
    private IBeaconPivot _beaconPivot = null!;
    private IBlockTree _blockTree = null!;
    private PosForwardHeaderProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _chainLevelHelper = Substitute.For<IChainLevelHelper>();
        // `ChainLevelHelper.GetStartingPoint` in the walk-back path returns the anchor at
        // `BestKnownNumber`, so `headers[0].Number == BestKnownNumber` (here `0`).
        _chainLevelHelper.GetNextHeaders(default, default, default).ReturnsForAnyArgs(_ => BuildSequentialHeaders(start: 0, count: CachedBatchSize));

        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.HasEverReachedTerminalBlock().Returns(true);

        _beaconPivot = Substitute.For<IBeaconPivot>();
        _beaconPivot.BeaconPivotExists().Returns(true);
        _beaconPivot.ProcessDestination = BuildHeader(1_000, TestItem.KeccakA);

        ISealValidator sealValidator = Substitute.For<ISealValidator>();
        sealValidator.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns(true);

        _blockTree = Substitute.For<IBlockTree>();
        _blockTree.BestKnownNumber.Returns(0UL);

        _provider = new PosForwardHeaderProvider(
            _chainLevelHelper,
            poSSwitcher,
            _beaconPivot,
            sealValidator,
            _blockTree,
            Substitute.For<ISyncPeerPool>(),
            new NullSyncReport(),
            LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _provider.UnsubscribeForTest();

    private Task<IOwnedReadOnlyList<BlockHeader?>?> Get(ulong skip = 0, ulong max = Requested) =>
        _provider.GetBlockHeaders(skip, max, CancellationToken.None);

    private void RaiseMainChainUpdate(params Block[] blocks)
    {
        List<BlockHeader> headers = new(blocks.Length);
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
        Assert.That(first!.Count, Is.EqualTo(Requested));
        Assert.That(first[0]!.Number, Is.EqualTo(0));

        _blockTree.BestKnownNumber.Returns(20UL);
        using IOwnedReadOnlyList<BlockHeader?>? second = await Get();

        Assert.That(second!.Count, Is.EqualTo(Requested));
        Assert.That(second[0]!.Number, Is.EqualTo(20));
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
}
