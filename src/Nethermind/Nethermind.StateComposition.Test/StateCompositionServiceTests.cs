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
using Nethermind.State;
using Nethermind.Trie;
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
        config.ScanCooldownSeconds.Returns(0);
        config.ExcludeStorage.Returns(false);
        return config;
    }

    [Test]
    public void Constructor_RejectsZeroParallelism()
    {
        IStateCompositionConfig config = CreateValidConfig();
        config.ScanParallelism.Returns(0);

        Assert.Throws<ArgumentException>(() =>
            new StateCompositionService(
                Substitute.For<IStateReader>(),
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
                Substitute.For<IStateReader>(),
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
                Substitute.For<IStateReader>(),
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
                Substitute.For<IStateReader>(),
                new StateCompositionStateHolder(),
                config,
                LimboLogs.Instance));
    }

    [Test]
    public async Task GetTrieDistributionAsync_FailsWhenNotInitialized()
    {
        StateCompositionService service = new(
            Substitute.For<IStateReader>(),
            new StateCompositionStateHolder(),
            CreateValidConfig(),
            LimboLogs.Instance);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        Result<TrieDepthDistribution> result =
            await service.GetTrieDistributionAsync(header, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.Error, Does.Contain("No cached data"));
        });
    }

    [Test]
    public void CancelScan_DoesNotThrowWhenNoScanRunning()
    {
        StateCompositionService service = new(
            Substitute.For<IStateReader>(),
            new StateCompositionStateHolder(),
            CreateValidConfig(),
            LimboLogs.Instance);

        Assert.DoesNotThrow(() => service.CancelScan());
    }

    // --- C-1: AnalyzeAsync integration test ---

    [Test]
    public async Task AnalyzeAsync_ReturnsStats_AndUpdatesStateHolder()
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        StateCompositionStateHolder stateHolder = new();

        StateCompositionService service = new(
            stateReader, stateHolder, CreateValidConfig(), LimboLogs.Instance);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        Result<StateCompositionStats> result = await service.AnalyzeAsync(header, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(stateHolder.IsInitialized, Is.True);
            Assert.That(stateHolder.IsScanning, Is.False);
            Assert.That(stateHolder.LastScanMetadata, Is.Not.Null);
            Assert.That(stateHolder.LastScanMetadata!.Value.IsComplete, Is.True);
        });
    }

    // --- C-2: InspectContractAsync tests ---

    [Test]
    public async Task InspectContractAsync_ReturnsNull_WhenAccountNotFound()
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        // TryGetAccount returns false by default (NSubstitute default for bool)

        StateCompositionService service = new(
            stateReader, new StateCompositionStateHolder(), CreateValidConfig(), LimboLogs.Instance);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        Result<TopContractEntry?> result = await service.InspectContractAsync(
            Address.Zero, header, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Data, Is.Null);
        });
    }

    [Test]
    public async Task InspectContractAsync_ReturnsNull_WhenNoStorage()
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        AccountStruct noStorageAccount = new(0, 0,
            Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);

        AccountStruct outAccount = default;
        stateReader.TryGetAccount(default!, default!, out outAccount)
            .ReturnsForAnyArgs(x =>
            {
                x[2] = noStorageAccount;
                return true;
            });

        StateCompositionService service = new(
            stateReader, new StateCompositionStateHolder(), CreateValidConfig(), LimboLogs.Instance);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        Result<TopContractEntry?> result = await service.InspectContractAsync(
            Address.Zero, header, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Data, Is.Null);
        });
    }

    [Test]
    public async Task InspectContractAsync_CompletesWithoutError_WhenHasStorage()
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        AccountStruct withStorageAccount = new(0, 0,
            Keccak.Zero.ValueHash256, Keccak.Zero.ValueHash256);

        AccountStruct outAccount = default;
        stateReader.TryGetAccount(default!, default!, out outAccount)
            .ReturnsForAnyArgs(x =>
            {
                x[2] = withStorageAccount;
                return true;
            });

        StateCompositionService service = new(
            stateReader, new StateCompositionStateHolder(), CreateValidConfig(), LimboLogs.Instance);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        // RunTreeVisitor mock is a no-op, so visitor finds nothing → returns null.
        // The important assertion is that the flow completes without error.
        Result<TopContractEntry?> result = await service.InspectContractAsync(
            Address.Zero, header, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Data, Is.Null);
        });
    }

    // --- H-6: Cooldown and semaphore rejection tests ---

    [Test]
    public async Task AnalyzeAsync_ReturnsCooldownError_AfterRecentScan()
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        IStateCompositionConfig config = CreateValidConfig();
        config.ScanCooldownSeconds.Returns(60);

        StateCompositionService service = new(
            stateReader, new StateCompositionStateHolder(), config, LimboLogs.Instance);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        // First scan completes immediately (mock RunTreeVisitor is a no-op)
        Result<StateCompositionStats> first = await service.AnalyzeAsync(header, CancellationToken.None);
        Assert.That(first.IsSuccess, Is.True);

        // Second scan should return cooldown error
        Result<StateCompositionStats> second = await service.AnalyzeAsync(header, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(second.IsError, Is.True);
            Assert.That(second.Error, Does.Contain("cooldown"));
        });
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task AnalyzeAsync_ReturnsScanInProgressError_WhenAlreadyRunning()
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        ManualResetEventSlim entered = new(false);
        TaskCompletionSource blocker = new();

        stateReader.WhenForAnyArgs(x =>
                x.RunTreeVisitor<StateCompositionContext>(default!, default, default))
            .Do(_ =>
            {
                entered.Set();
                blocker.Task.GetAwaiter().GetResult();
            });

        StateCompositionService service = new(
            stateReader, new StateCompositionStateHolder(), CreateValidConfig(), LimboLogs.Instance);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        // Start first scan — blocks inside RunTreeVisitor
        Task<Result<StateCompositionStats>> firstScan = service.AnalyzeAsync(header, CancellationToken.None);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True, "First scan did not enter RunTreeVisitor");

        // Second scan should return error immediately (fail-fast semaphore)
        Result<StateCompositionStats> second = await service.AnalyzeAsync(header, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(second.IsError, Is.True);
            Assert.That(second.Error, Does.Contain("already in progress"));
        });

        // Release blocker so first scan completes
        blocker.SetResult();
        await firstScan;
    }

    // --- InspectContractAsync concurrency test ---

    [Test]
    public async Task InspectContractAsync_ReturnsFail_WhenInspectionAlreadyRunning()
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        ManualResetEventSlim entered = new(false);
        TaskCompletionSource blocker = new();

        AccountStruct withStorageAccount = new(0, 0,
            Keccak.Zero.ValueHash256, Keccak.Zero.ValueHash256);

        stateReader.TryGetAccount(default!, default!, out Arg.Any<AccountStruct>())
            .ReturnsForAnyArgs(x =>
            {
                x[2] = withStorageAccount;
                return true;
            });

        stateReader.WhenForAnyArgs(x =>
                x.RunTreeVisitor<StateCompositionContext>(default!, default, default))
            .Do(_ =>
            {
                entered.Set();
                blocker.Task.GetAwaiter().GetResult();
            });

        StateCompositionService service = new(
            stateReader, new StateCompositionStateHolder(), CreateValidConfig(), LimboLogs.Instance);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        Task<Result<TopContractEntry?>> firstInspect =
            service.InspectContractAsync(Address.Zero, header, CancellationToken.None);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);

        Result<TopContractEntry?> second =
            await service.InspectContractAsync(Address.Zero, header, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(second.IsError, Is.True);
            Assert.That(second.Error, Does.Contain("inspection already in progress"));
        });

        blocker.SetResult();
        await firstInspect;
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

    [Test]
    public async Task InspectContract_Fails_WhenAddressNull()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.TestObject);

        StateCompositionRpcModule rpc = new(
            Substitute.For<IStateCompositionService>(),
            Substitute.For<IStateCompositionStateHolder>(),
            blockTree);

        JsonRpc.ResultWrapper<TopContractEntry?> result = await rpc.statecomp_inspectContract(null!);

        Assert.Multiple(() =>
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.Result.Error, Does.Contain("Address parameter is required"));
        });
    }
}
