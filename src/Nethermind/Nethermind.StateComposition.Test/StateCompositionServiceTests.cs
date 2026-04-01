// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test;

[TestFixture]
public class StateCompositionServiceTests
{
    private static IStateCompositionConfig CreateValidConfig()
    {
        IStateCompositionConfig config = Substitute.For<IStateCompositionConfig>();
        config.ScanParallelism.Returns(4);
        config.ScanMemoryBudget.Returns(1_000_000_000L);
        config.ScanQueueTimeoutSeconds.Returns(5);
        config.TopNContracts.Returns(20);
        config.ExcludeStorage.Returns(false);
        return config;
    }

    [Test]
    public void Constructor_RejectsZeroParallelism()
    {
        IStateCompositionConfig config = CreateValidConfig();
        config.ScanParallelism.Returns(0);

        Assert.Throws<ArgumentException>(() =>
        {
            _ = new StateCompositionService(
                Substitute.For<IStateReader>(),
                new StateCompositionStateHolder(),
                config,
                LimboLogs.Instance);
        });
    }

    [Test]
    public void Constructor_RejectsZeroMemoryBudget()
    {
        IStateCompositionConfig config = CreateValidConfig();
        config.ScanMemoryBudget.Returns(0L);

        Assert.Throws<ArgumentException>(() =>
            _ = new StateCompositionService(
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
            _ = new StateCompositionService(
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
            _ = new StateCompositionService(
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

        Result<TrieDepthDistribution> result =
            await service.GetTrieDistributionAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.Error, Does.Contain("No cached data"));
        }
    }

    [Test]
    public void CancelScan_DoesNotThrowWhenNoScanRunning()
    {
        StateCompositionService service = new(
            Substitute.For<IStateReader>(),
            new StateCompositionStateHolder(),
            CreateValidConfig(),
            LimboLogs.Instance);

        Assert.DoesNotThrow(service.CancelScan);
    }

    [Test]
    public async Task AnalyzeAsync_ReturnsStats_AndUpdatesStateHolder()
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        StateCompositionStateHolder stateHolder = new();

        StateCompositionService service = new(
            stateReader, stateHolder, CreateValidConfig(), LimboLogs.Instance);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        Result<StateCompositionStats> result = await service.AnalyzeAsync(header, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(stateHolder.IsInitialized, Is.True);
            Assert.That(stateHolder.LastScanMetadata, Is.Not.Null);
            Assert.That(stateHolder.LastScanMetadata!.Value.IsComplete, Is.True);
        }
    }

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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Data, Is.Null);
        }
    }

    [Test]
    public async Task InspectContractAsync_ReturnsNull_WhenNoStorage()
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        AccountStruct noStorageAccount = new(0, 0,
            Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);

        stateReader.TryGetAccount(null!, null!, out AccountStruct _)
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Data, Is.Null);
        }
    }

    [Test]
    public async Task InspectContractAsync_CompletesWithoutError_WhenHasStorage()
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        AccountStruct withStorageAccount = new(0, 0,
            Keccak.Zero.ValueHash256, Keccak.Zero.ValueHash256);

        stateReader.TryGetAccount(null!, null!, out AccountStruct _)
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Data, Is.Null);
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task AnalyzeAsync_ReturnsScanInProgressError_WhenAlreadyRunning()
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        ManualResetEventSlim entered = new(false);
        TaskCompletionSource blocker = new();

        stateReader.WhenForAnyArgs(x =>
                x.RunTreeVisitor<StateCompositionContext>(null!, null))
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.IsError, Is.True);
            Assert.That(second.Error, Does.Contain("already in progress"));
        }

        // Release blocker so first scan completes
        blocker.SetResult();
        await firstScan;
    }

    [Test]
    public async Task InspectContractAsync_ReturnsFail_WhenInspectionAlreadyRunning()
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        ManualResetEventSlim entered = new(false);
        TaskCompletionSource blocker = new();

        AccountStruct withStorageAccount = new(0, 0,
            Keccak.Zero.ValueHash256, Keccak.Zero.ValueHash256);

        stateReader.TryGetAccount(null!, null!, out Arg.Any<AccountStruct>())
            .ReturnsForAnyArgs(x =>
            {
                x[2] = withStorageAccount;
                return true;
            });

        stateReader.WhenForAnyArgs(x =>
                x.RunTreeVisitor<StateCompositionContext>(null!, null))
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.IsError, Is.True);
            Assert.That(second.Error, Does.Contain("inspection already in progress"));
        }

        blocker.SetResult();
        await firstInspect;
    }
}
