// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test;

[TestFixture]
public class StateCompositionRpcModuleTests
{
    [Test]
    public async Task GetCachedStats_ReturnsNullStats_WhenNotInitialized()
    {
        IStateCompositionStateHolder stateHolder = Substitute.For<IStateCompositionStateHolder>();
        stateHolder.IsInitialized.Returns(false);

        StateCompositionRpcModule rpc = new(
            Substitute.For<IStateCompositionService>(),
            stateHolder,
            Substitute.For<IBlockTree>());

        JsonRpc.ResultWrapper<CachedStatsResponse> result = await rpc.statecomp_getCachedStats();

        Assert.That(result.Data.Stats, Is.Null);
    }

    [Test]
    public async Task GetCacheMetadata_ReturnsNull_WhenNeverScanned()
    {
        IStateCompositionStateHolder stateHolder = Substitute.For<IStateCompositionStateHolder>();
        stateHolder.LastScanMetadata.Returns((ScanMetadata?)null);

        StateCompositionRpcModule rpc = new(
            Substitute.For<IStateCompositionService>(),
            stateHolder,
            Substitute.For<IBlockTree>());

        JsonRpc.ResultWrapper<ScanMetadata?> result = await rpc.statecomp_getCacheMetadata();

        Assert.That(result.Data, Is.Null);
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
