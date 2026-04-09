// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test;

[TestFixture]
public class StateCompositionRpcModuleTests
{
    // Minimal subclass that lets tests override only what they need without
    // satisfying the full StateCompositionService constructor.
    private class FakeService : StateCompositionService
    {
        public bool CancelScanCalled { get; private set; }

        public FakeService() : base(
            Substitute.For<Nethermind.State.IStateReader>(),
            Substitute.For<Nethermind.State.IWorldStateManager>(),
            Substitute.For<IBlockTree>(),
            new StateCompositionStateHolder(),
            new StateCompositionSnapshotStore(new MemDb()),
            Substitute.For<IStateCompositionConfig>(),
            Nethermind.Logging.LimboLogs.Instance)
        { }

        public override void CancelScan()
        {
            CancelScanCalled = true;
        }
    }

    [Test]
    public async Task GetCachedStats_ReturnsNullStats_WhenNotInitialized()
    {
        StateCompositionStateHolder stateHolder = new();

        StateCompositionRpcModule rpc = new(
            new FakeService(),
            stateHolder,
            Substitute.For<IBlockTree>(),
            new StateCompositionSnapshotStore(new MemDb()));

        JsonRpc.ResultWrapper<CachedStatsResponse> result = await rpc.statecomp_getCachedStats();

        Assert.That(result.Data.CurrentStats, Is.Null);
    }

    [Test]
    public async Task GetCacheMetadata_ReturnsNull_WhenNeverScanned()
    {
        StateCompositionStateHolder stateHolder = new();

        StateCompositionRpcModule rpc = new(
            new FakeService(),
            stateHolder,
            Substitute.For<IBlockTree>(),
            new StateCompositionSnapshotStore(new MemDb()));

        JsonRpc.ResultWrapper<ScanMetadata?> result = await rpc.statecomp_getCacheMetadata();

        Assert.That(result.Data, Is.Null);
    }

    [Test]
    public async Task CancelScan_ReturnsTrue()
    {
        FakeService service = new();

        StateCompositionRpcModule rpc = new(
            service,
            new StateCompositionStateHolder(),
            Substitute.For<IBlockTree>(),
            new StateCompositionSnapshotStore(new MemDb()));

        JsonRpc.ResultWrapper<bool> result = await rpc.statecomp_cancelScan();

        Assert.That(result.Data, Is.True);
        Assert.That(service.CancelScanCalled, Is.True);
    }

    [Test]
    public async Task GetStats_Fails_WhenNoHeadBlock()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns((Block?)null);

        StateCompositionRpcModule rpc = new(
            new FakeService(),
            new StateCompositionStateHolder(),
            blockTree,
            new StateCompositionSnapshotStore(new MemDb()));

        JsonRpc.ResultWrapper<StateCompositionStats> result = await rpc.statecomp_getStats();

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
    }

    [Test]
    public async Task InspectContract_Fails_WhenAddressNull()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.TestObject);

        StateCompositionRpcModule rpc = new(
            new FakeService(),
            new StateCompositionStateHolder(),
            blockTree,
            new StateCompositionSnapshotStore(new MemDb()));

        JsonRpc.ResultWrapper<TopContractEntry?> result = await rpc.statecomp_inspectContract(null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.Result.Error, Does.Contain("Address parameter is required"));
        }
    }
}
