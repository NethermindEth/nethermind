// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Rpc;
using Nethermind.StateComposition.Service;
using Nethermind.StateComposition.Snapshots;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Rpc;

[TestFixture]
public class StateCompositionRpcModuleTests
{
    private static StateCompositionService CreateService() => new(
        Substitute.For<IStateReader>(),
        Substitute.For<IWorldStateManager>(),
        Substitute.For<IBlockTree>(),
        new StateCompositionStateHolder(),
        new StateCompositionSnapshotStore(new MemDb(), LimboLogs.Instance),
        Substitute.For<IStateCompositionConfig>(),
        LimboLogs.Instance);

    private static IStateCompositionConfig CreateConfig() => new StateCompositionConfig();

    [Test]
    public async Task GetCachedStats_ReturnsDefaultStats_WhenNotInitialized()
    {
        StateCompositionStateHolder stateHolder = new();

        StateCompositionRpcModule rpc = new(
            CreateService(),
            stateHolder,
            Substitute.For<IBlockTree>(),
            CreateConfig());

        ResultWrapper<StateCompositionReport> result = await rpc.statecomp_get();

        Assert.That(result.Data.LastScanMetadata.IsComplete, Is.False);
    }

    [Test]
    public async Task CancelScan_ReturnsFalse_WhenNoScanActive()
    {
        StateCompositionRpcModule rpc = new(
            CreateService(),
            new StateCompositionStateHolder(),
            Substitute.For<IBlockTree>(),
            CreateConfig());

        ResultWrapper<bool> result = await rpc.statecomp_cancelScan();

        Assert.That(result.Data, Is.False);
    }

    [Test]
    public async Task InspectContract_Fails_WhenAddressNull()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.TestObject);

        StateCompositionRpcModule rpc = new(
            CreateService(),
            new StateCompositionStateHolder(),
            blockTree,
            CreateConfig());

        ResultWrapper<TopContractEntry?> result = await rpc.statecomp_inspectContract(null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.Result.Error, Does.Contain("Address parameter is required"));
        }
    }
}
