// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
        // `ChainLevelHelper.GetStartingPoint` in the typical walk-back path returns the anchor at
        // `BestKnownNumber`, so the first header is `BestKnownNumber` (here `0`), not `BestKnownNumber + 1`.
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

        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        ISyncReport syncReport = new NullSyncReport();

        _provider = new PosForwardHeaderProvider(
            _chainLevelHelper,
            poSSwitcher,
            _beaconPivot,
            sealValidator,
            _blockTree,
            syncPeerPool,
            syncReport,
            LimboLogs.Instance);
    }

    [Test]
    public async Task Second_call_with_same_inputs_is_served_from_cache()
    {
        await _provider.GetBlockHeaders(0, Requested, CancellationToken.None);
        await _provider.GetBlockHeaders(0, Requested, CancellationToken.None);

        _chainLevelHelper.ReceivedWithAnyArgs(1).GetNextHeaders(default, default, default);
    }

    [Test]
    public async Task Cache_is_invalidated_when_process_destination_hash_changes()
    {
        await _provider.GetBlockHeaders(0, Requested, CancellationToken.None);

        _beaconPivot.ProcessDestination = BuildHeader(1_000, TestItem.KeccakB);
        await _provider.GetBlockHeaders(0, Requested, CancellationToken.None);

        _chainLevelHelper.ReceivedWithAnyArgs(2).GetNextHeaders(default, default, default);
    }

    [Test]
    public async Task Cache_miss_when_best_known_number_advances_past_cached_range()
    {
        await _provider.GetBlockHeaders(0, Requested, CancellationToken.None);

        _blockTree.BestKnownNumber.Returns((long)CachedBatchSize + 10);
        await _provider.GetBlockHeaders(0, Requested, CancellationToken.None);

        _chainLevelHelper.ReceivedWithAnyArgs(2).GetNextHeaders(default, default, default);
    }

    [Test]
    public async Task Cached_slice_advances_with_best_known_number()
    {
        using IOwnedReadOnlyList<BlockHeader?>? first = await _provider.GetBlockHeaders(0, Requested, CancellationToken.None);
        first!.Count.Should().Be(Requested);
        first[0]!.Number.Should().Be(0);

        _blockTree.BestKnownNumber.Returns(20L);
        using IOwnedReadOnlyList<BlockHeader?>? second = await _provider.GetBlockHeaders(0, Requested, CancellationToken.None);

        second!.Count.Should().Be(Requested);
        second[0]!.Number.Should().Be(20);
        _chainLevelHelper.ReceivedWithAnyArgs(1).GetNextHeaders(default, default, default);
    }

    [Test]
    public async Task SkipLastN_is_honored_on_cache_hit()
    {
        await _provider.GetBlockHeaders(0, Requested, CancellationToken.None);

        const int skip = 4;
        using IOwnedReadOnlyList<BlockHeader?>? sliced = await _provider.GetBlockHeaders(skip, Requested, CancellationToken.None);

        sliced!.Count.Should().BeLessThanOrEqualTo(CachedBatchSize - skip);
        sliced[^1]!.Number.Should().BeLessThanOrEqualTo(CachedBatchSize - skip);
        _chainLevelHelper.ReceivedWithAnyArgs(1).GetNextHeaders(default, default, default);
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
