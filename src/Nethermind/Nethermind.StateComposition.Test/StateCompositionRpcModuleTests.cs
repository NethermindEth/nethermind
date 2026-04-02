// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test;

[TestFixture]
public class StateCompositionRpcModuleTests
{
    [Test]
    public async Task GetCachedStats_ReturnsNullStats_WhenNoScans()
    {
        IStateCompositionStateHolder stateHolder = Substitute.For<IStateCompositionStateHolder>();
        stateHolder.GetScan(null).Returns((ScanCacheEntry?)null);
        stateHolder.ListScans().Returns(new List<ScanMetadata>());

        StateCompositionRpcModule rpc = new(
            Substitute.For<IStateCompositionService>(),
            stateHolder,
            Substitute.For<IBlockTree>());

        JsonRpc.ResultWrapper<CachedStatsResponse> result = await rpc.statecomp_getCachedStats();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data.Stats, Is.Null);
            Assert.That(result.Data.AvailableScans, Is.Empty);
        }
    }

    [Test]
    public async Task GetCachedStats_WithBlockParam_ReturnsMatchingScan()
    {
        IStateCompositionStateHolder stateHolder = Substitute.For<IStateCompositionStateHolder>();
        StateCompositionStats stats = new() { AccountsTotal = 42 };
        stateHolder.GetScan(100L).Returns(new ScanCacheEntry { Stats = stats });
        stateHolder.ListScans().Returns(new List<ScanMetadata>());

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        blockTree.FindHeader(Arg.Any<BlockParameter>()).Returns(header);

        StateCompositionRpcModule rpc = new(
            Substitute.For<IStateCompositionService>(),
            stateHolder,
            blockTree);

        JsonRpc.ResultWrapper<CachedStatsResponse> result =
            await rpc.statecomp_getCachedStats(new BlockParameter(100));

        Assert.That(result.Data.Stats?.AccountsTotal, Is.EqualTo(42));
    }

    [Test]
    public async Task ListScans_ReturnsEmptyList_WhenNoScans()
    {
        IStateCompositionStateHolder stateHolder = Substitute.For<IStateCompositionStateHolder>();
        stateHolder.ListScans().Returns(new List<ScanMetadata>());

        StateCompositionRpcModule rpc = new(
            Substitute.For<IStateCompositionService>(),
            stateHolder,
            Substitute.For<IBlockTree>());

        JsonRpc.ResultWrapper<IReadOnlyList<ScanMetadata>> result = await rpc.statecomp_listScans();

        Assert.That(result.Data, Is.Empty);
    }

    [Test]
    public async Task CancelScan_ReturnsTrue()
    {
        IStateCompositionService service = Substitute.For<IStateCompositionService>();

        StateCompositionRpcModule rpc = new(
            service,
            Substitute.For<IStateCompositionStateHolder>(),
            Substitute.For<IBlockTree>());

        JsonRpc.ResultWrapper<bool> result = await rpc.statecomp_cancelScan();

        Assert.That(result.Data, Is.True);
        service.Received(1).CancelScan();
    }

    [Test]
    public async Task GetStats_Fails_WhenNoHeadBlock()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns((Block?)null);

        StateCompositionRpcModule rpc = new(
            Substitute.For<IStateCompositionService>(),
            Substitute.For<IStateCompositionStateHolder>(),
            blockTree);

        JsonRpc.ResultWrapper<StateCompositionStats> result = await rpc.statecomp_getStats();

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
    }

    [Test]
    public async Task GetStats_WithBlockParam_ResolvesHeader()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        BlockHeader header = Build.A.BlockHeader.WithNumber(500).TestObject;
        blockTree.FindHeader(Arg.Any<BlockParameter>()).Returns(header);

        IStateCompositionService service = Substitute.For<IStateCompositionService>();
        service.AnalyzeAsync(header, default)
            .ReturnsForAnyArgs(Result<StateCompositionStats>.Success(new StateCompositionStats()));

        StateCompositionRpcModule rpc = new(service,
            Substitute.For<IStateCompositionStateHolder>(), blockTree);

        JsonRpc.ResultWrapper<StateCompositionStats> result =
            await rpc.statecomp_getStats(new BlockParameter(500));

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
    }

    [Test]
    public async Task GetStats_WithBlockParam_Fails_WhenBlockNotFound()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<BlockParameter>()).Returns((BlockHeader?)null);

        StateCompositionRpcModule rpc = new(
            Substitute.For<IStateCompositionService>(),
            Substitute.For<IStateCompositionStateHolder>(),
            blockTree);

        JsonRpc.ResultWrapper<StateCompositionStats> result =
            await rpc.statecomp_getStats(new BlockParameter(999999999));

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
    }

    [Test]
    public async Task InspectContract_Fails_WhenAddressNull()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.TestObject);

        StateCompositionRpcModule rpc = new(
            Substitute.For<IStateCompositionService>(),
            Substitute.For<IStateCompositionStateHolder>(),
            blockTree);

        JsonRpc.ResultWrapper<TopContractEntry?> result = await rpc.statecomp_inspectContract(null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.Result.Error, Does.Contain("Address parameter is required"));
        }
    }
}
