// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test;

[TestFixture]
public class StateCompositionServiceTests
{
    private IStateCompositionConfig CreateValidConfig()
    {
        IStateCompositionConfig config = Substitute.For<IStateCompositionConfig>();
        config.ScanParallelism.Returns(4);
        config.ScanMemoryBudget.Returns(1_000_000_000L);
        config.ScanQueueTimeoutSeconds.Returns(5);
        config.TopNContracts.Returns(20);
        return config;
    }

    [Test]
    public void Constructor_RejectsZeroParallelism()
    {
        IStateCompositionConfig config = CreateValidConfig();
        config.ScanParallelism.Returns(0);

        Assert.Throws<ArgumentException>(() =>
            new StateCompositionService(
                Substitute.For<State.IStateReader>(),
                new StateCompositionStateHolder(),
                config,
                LimboLogs.Instance));
    }

    [Test]
    public void Constructor_RejectsZeroMemoryBudget()
    {
        IStateCompositionConfig config = CreateValidConfig();
        config.ScanMemoryBudget.Returns(0L);

        Assert.Throws<ArgumentException>(() =>
            new StateCompositionService(
                Substitute.For<State.IStateReader>(),
                new StateCompositionStateHolder(),
                config,
                LimboLogs.Instance));
    }

    [Test]
    public void Constructor_RejectsZeroTimeout()
    {
        IStateCompositionConfig config = CreateValidConfig();
        config.ScanQueueTimeoutSeconds.Returns(0);

        Assert.Throws<ArgumentException>(() =>
            new StateCompositionService(
                Substitute.For<State.IStateReader>(),
                new StateCompositionStateHolder(),
                config,
                LimboLogs.Instance));
    }

    [Test]
    public void Constructor_RejectsZeroTopN()
    {
        IStateCompositionConfig config = CreateValidConfig();
        config.TopNContracts.Returns(0);

        Assert.Throws<ArgumentException>(() =>
            new StateCompositionService(
                Substitute.For<State.IStateReader>(),
                new StateCompositionStateHolder(),
                config,
                LimboLogs.Instance));
    }

    [Test]
    public void GetTrieDistributionAsync_ThrowsWhenNotInitialized()
    {
        StateCompositionService service = new(
            Substitute.For<State.IStateReader>(),
            new StateCompositionStateHolder(),
            CreateValidConfig(),
            LimboLogs.Instance);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.GetTrieDistributionAsync(header, CancellationToken.None));
    }

    [Test]
    public void CancelScan_DoesNotThrowWhenNoScanRunning()
    {
        StateCompositionService service = new(
            Substitute.For<State.IStateReader>(),
            new StateCompositionStateHolder(),
            CreateValidConfig(),
            LimboLogs.Instance);

        Assert.DoesNotThrow(() => service.CancelScan());
    }
}

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
    public async Task GetTrieDistribution_Fails_WhenNoHeadBlock()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns((Block?)null);

        StateCompositionRpcModule rpc = new(
            Substitute.For<IStateCompositionService>(),
            Substitute.For<IStateCompositionStateHolder>(),
            blockTree);

        JsonRpc.ResultWrapper<TrieDepthDistribution> result = await rpc.statecomp_getTrieDistribution();

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
    }
}
