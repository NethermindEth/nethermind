// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        _blockTree.BestKnownNumber.Returns(0);

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
    public void TearDown() => _provider.Dispose();

    private Task<IOwnedReadOnlyList<BlockHeader?>?> Get(int skip = 0, int max = Requested) =>
        _provider.GetBlockHeaders(skip, max, CancellationToken.None);

    private void RaiseMainChainUpdate(params Block[] blocks) =>
        _blockTree.OnUpdateMainChain += Raise.EventWith(_blockTree, new OnUpdateMainChainArgs(new List<Block>(blocks), wereProcessed: true));

    private void AssertChainLevelCalls(int expected) =>
        _chainLevelHelper.ReceivedWithAnyArgs(expected).GetNextHeaders(default, default, default);

    [Test]
    public async Task Second_call_with_same_inputs_is_served_from_cache()
    {
        using IOwnedReadOnlyList<BlockHeader?>? first = await Get();
        using IOwnedReadOnlyList<BlockHeader?>? second = await Get();

        AssertChainLevelCalls(1);
    }

    [Test]
    public async Task Cache_is_invalidated_when_process_destination_hash_changes()
    {
        using IOwnedReadOnlyList<BlockHeader?>? first = await Get();

        _beaconPivot.ProcessDestination = BuildHeader(1_000, TestItem.KeccakB);
        using IOwnedReadOnlyList<BlockHeader?>? second = await Get();

        AssertChainLevelCalls(2);
    }

    [Test]
    public async Task Cache_miss_when_best_known_number_advances_past_cached_range()
    {
        using IOwnedReadOnlyList<BlockHeader?>? first = await Get();

        _blockTree.BestKnownNumber.Returns((long)CachedBatchSize + 10);
        using IOwnedReadOnlyList<BlockHeader?>? second = await Get();

        AssertChainLevelCalls(2);
    }

    [Test]
    public async Task Cached_slice_advances_with_best_known_number()
    {
        using IOwnedReadOnlyList<BlockHeader?>? first = await Get();
        Assert.That(first!.Count, Is.EqualTo(Requested));
        Assert.That(first[0]!.Number, Is.EqualTo(0));

        _blockTree.BestKnownNumber.Returns(20L);
        using IOwnedReadOnlyList<BlockHeader?>? second = await Get();

        Assert.That(second!.Count, Is.EqualTo(Requested));
        Assert.That(second[0]!.Number, Is.EqualTo(20));
        AssertChainLevelCalls(1);
    }

    [Test]
    public async Task SkipLastN_is_honored_on_cache_hit()
    {
        using IOwnedReadOnlyList<BlockHeader?>? first = await Get();

        // Request more than the cache holds after the skip-tail trim so the front-cap
        // exposes `skipLastN`; otherwise `min(available, maxHeader) == maxHeader` and a silently
        // dropped `skipLastN` would still satisfy the assertion.
        const int skip = 4;
        using IOwnedReadOnlyList<BlockHeader?>? sliced = await Get(skip: skip, max: CachedBatchSize);

        Assert.That(sliced!.Count, Is.EqualTo(CachedBatchSize - skip));
        Assert.That(sliced[^1]!.Number, Is.EqualTo(CachedBatchSize - skip - 1));
        AssertChainLevelCalls(1);
    }

    [Test]
    public async Task Cache_is_invalidated_on_reorg_within_cached_range()
    {
        using IOwnedReadOnlyList<BlockHeader?>? first = await Get();

        Block reorgBlock = Build.A.Block.WithNumber(10).WithDifficulty(2).TestObject;
        Assert.That(reorgBlock.Header.Hash, Is.Not.EqualTo(first![10]!.Hash!));
        RaiseMainChainUpdate(reorgBlock);

        using IOwnedReadOnlyList<BlockHeader?>? second = await Get();

        AssertChainLevelCalls(2);
    }

    [Test]
    public async Task Cache_survives_main_chain_update_that_matches_cached_hash()
    {
        using IOwnedReadOnlyList<BlockHeader?>? first = await Get();

        Block extensionBlock = new(first![5]!, new BlockBody());
        RaiseMainChainUpdate(extensionBlock);

        using IOwnedReadOnlyList<BlockHeader?>? second = await Get();

        AssertChainLevelCalls(1);
    }

    private static BlockHeader[] BuildSequentialHeaders(long start, int count)
    {
        BlockHeader[] headers = new BlockHeader[count];
        BlockHeader? parent = null;
        for (int i = 0; i < count; i++)
        {
            BlockHeaderBuilder builder = Build.A.BlockHeader.WithNumber(start + i);
            if (parent is not null) builder = builder.WithParent(parent);
            headers[i] = builder.TestObject;
            parent = headers[i];
        }
        return headers;
    }

    private static BlockHeader BuildHeader(long number, Hash256 hash) =>
        Build.A.BlockHeader.WithNumber(number).WithHash(hash).TestObject;
}
