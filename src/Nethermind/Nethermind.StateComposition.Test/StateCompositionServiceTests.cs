// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test;

[TestFixture]
public class StateCompositionServiceTests
{
    private static StateCompositionSnapshotStore CreateSnapshotStore() => new(new MemDb());

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
    public void Constructor_ClampsZeroParallelism_DoesNotThrow()
    {
        IStateCompositionConfig config = CreateValidConfig();
        config.ScanParallelism.Returns(0);

        // Invalid value is clamped to 1 — no exception on construction.
        Assert.DoesNotThrow(() =>
        {
            using StateCompositionService _ = new(
                Substitute.For<IStateReader>(),
                Substitute.For<IWorldStateManager>(),
                Substitute.For<IBlockTree>(),
                new StateCompositionStateHolder(),
                CreateSnapshotStore(),
                config,
                LimboLogs.Instance);
        });
    }

    [Test]
    public void Constructor_ClampsZeroMemoryBudget_DoesNotThrow()
    {
        IStateCompositionConfig config = CreateValidConfig();
        config.ScanMemoryBudget.Returns(0L);

        Assert.DoesNotThrow(() =>
        {
            using StateCompositionService _ = new(
                Substitute.For<IStateReader>(),
                Substitute.For<IWorldStateManager>(),
                Substitute.For<IBlockTree>(),
                new StateCompositionStateHolder(),
                CreateSnapshotStore(),
                config,
                LimboLogs.Instance);
        });
    }

    [Test]
    public void Constructor_ClampsZeroTopN_DoesNotThrow()
    {
        IStateCompositionConfig config = CreateValidConfig();
        config.TopNContracts.Returns(0);

        Assert.DoesNotThrow(() =>
        {
            using StateCompositionService _ = new(
                Substitute.For<IStateReader>(),
                Substitute.For<IWorldStateManager>(),
                Substitute.For<IBlockTree>(),
                new StateCompositionStateHolder(),
                CreateSnapshotStore(),
                config,
                LimboLogs.Instance);
        });
    }

    [Test]
    public void GetTrieDistribution_FailsWhenNotInitialized()
    {
        StateCompositionService service = new(
            Substitute.For<IStateReader>(),
            Substitute.For<IWorldStateManager>(),
            Substitute.For<IBlockTree>(),
            new StateCompositionStateHolder(),
            CreateSnapshotStore(),
            CreateValidConfig(),
            LimboLogs.Instance);

        Result<TrieDepthDistribution> result = service.GetTrieDistribution();

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
            Substitute.For<IWorldStateManager>(),
            Substitute.For<IBlockTree>(),
            new StateCompositionStateHolder(),
            CreateSnapshotStore(),
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
            stateReader, Substitute.For<IWorldStateManager>(), Substitute.For<IBlockTree>(),
            stateHolder, CreateSnapshotStore(), CreateValidConfig(), LimboLogs.Instance);

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
            stateReader, Substitute.For<IWorldStateManager>(), Substitute.For<IBlockTree>(),
            new StateCompositionStateHolder(), CreateSnapshotStore(), CreateValidConfig(), LimboLogs.Instance);

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
            stateReader, Substitute.For<IWorldStateManager>(), Substitute.For<IBlockTree>(),
            new StateCompositionStateHolder(), CreateSnapshotStore(), CreateValidConfig(), LimboLogs.Instance);

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
            stateReader, Substitute.For<IWorldStateManager>(), Substitute.For<IBlockTree>(),
            new StateCompositionStateHolder(), CreateSnapshotStore(), CreateValidConfig(), LimboLogs.Instance);

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
            stateReader, Substitute.For<IWorldStateManager>(), Substitute.For<IBlockTree>(),
            new StateCompositionStateHolder(), CreateSnapshotStore(), CreateValidConfig(), LimboLogs.Instance);

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
            stateReader, Substitute.For<IWorldStateManager>(), Substitute.For<IBlockTree>(),
            new StateCompositionStateHolder(), CreateSnapshotStore(), CreateValidConfig(), LimboLogs.Instance);

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

    // ── Item 4: Cancellation mid-scan ──────────────────────────────────────────

    /// <summary>
    /// When the CancellationToken passed to AnalyzeAsync is cancelled while the
    /// visitor is in-flight, the task must complete (not hang) and must NOT mark
    /// the baseline as initialized.
    ///
    /// The mock visitor throws OperationCanceledException when the scan token is
    /// cancelled — this is the cooperative cancellation contract any real visitor
    /// must honour. The service propagates the exception; no Result.Success is produced.
    /// </summary>
    [Test]
    [CancelAfter(5_000)]
    public async Task AnalyzeAsync_CancelledMidScan_CompletesWithoutHangAndNoInitialization(CancellationToken testCt)
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        ManualResetEventSlim visitorEntered = new(false);
        // Capture the linked token so the mock can throw when it's cancelled.
        CancellationToken capturedToken = default;

        stateReader.WhenForAnyArgs(x =>
                x.RunTreeVisitor<StateCompositionContext>(null!, null))
            .Do(ci =>
            {
                // The visitor passed in is the StateCompositionVisitor; grab its token
                // via the captured CTS token set before the call.
                visitorEntered.Set();
                // Block until cancelled — respects cooperative cancellation
                capturedToken.WaitHandle.WaitOne();
                capturedToken.ThrowIfCancellationRequested();
            });

        StateCompositionStateHolder stateHolder = new();
        using CancellationTokenSource cts = new();

        // We need to capture the linked token. Intercept it by wrapping: since
        // AnalyzeAsync creates the linked CTS internally, the simplest approach is
        // to cancel cts (passed in) which cancels the linked token too.
        // The mock above waits on capturedToken — set it to cts.Token directly.
        capturedToken = cts.Token;

        StateCompositionService service = new(
            stateReader, Substitute.For<IWorldStateManager>(), Substitute.For<IBlockTree>(),
            stateHolder, CreateSnapshotStore(), CreateValidConfig(), LimboLogs.Instance);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        Task<Result<StateCompositionStats>> scanTask = service.AnalyzeAsync(header, cts.Token);

        // Wait for visitor to be in-flight
        Assert.That(visitorEntered.Wait(TimeSpan.FromSeconds(3)), Is.True, "Visitor did not enter RunTreeVisitor");

        // Cancel — the mock will unblock and throw OperationCanceledException
        cts.Cancel();

        // Task must complete without hanging; the cancellation propagates as exception
        try
        {
            await scanTask.WaitAsync(TimeSpan.FromSeconds(3), testCt);
            // If we reach here the task returned a Result rather than throwing —
            // that's also acceptable as long as IsInitialized is false.
        }
        catch (OperationCanceledException)
        {
            // Expected: task faulted with cancellation
        }

        // Baseline must NOT have been installed — partial scan must not corrupt state
        Assert.That(stateHolder.IsInitialized, Is.False,
            "Baseline must not be marked complete after mid-scan cancellation");
    }

    // ── Item 5 is in TrieDiffWalkerTests.cs ───────────────────────────────────

    // ── Item 6: Cross-semaphore interaction ───────────────────────────────────

    /// <summary>
    /// AnalyzeAsync and InspectContractAsync use SEPARATE semaphores (_scanLock vs _inspectLock).
    /// While a scan is blocked, an inspection call must not hang — it acquires _inspectLock independently.
    /// </summary>
    [Test]
    [CancelAfter(5_000)]
    public async Task InspectContractAsync_CompletesIndependently_WhileAnalyzeAsyncIsBlocked(CancellationToken testCt)
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        ManualResetEventSlim scanEntered = new(false);
        ManualResetEventSlim releaseScan = new(false);

        // Block only the scan visitor (StateCompositionContext), not SingleContractVisitor
        stateReader.WhenForAnyArgs(x =>
                x.RunTreeVisitor<StateCompositionContext>(null!, null))
            .Do(_ =>
            {
                scanEntered.Set();
                releaseScan.Wait();
            });

        // InspectContractAsync will call TryGetAccount first — return null account so it exits early
        stateReader.TryGetAccount(null!, null!, out Arg.Any<AccountStruct>())
            .ReturnsForAnyArgs(false);

        using StateCompositionService service = new(
            stateReader, Substitute.For<IWorldStateManager>(), Substitute.For<IBlockTree>(),
            new StateCompositionStateHolder(), CreateSnapshotStore(), CreateValidConfig(), LimboLogs.Instance);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        // Start the scan — it blocks inside RunTreeVisitor<StateCompositionContext>
        Task<Result<StateCompositionStats>> scanTask = service.AnalyzeAsync(header, testCt);
        Assert.That(scanEntered.Wait(TimeSpan.FromSeconds(3)), Is.True, "Scan did not enter RunTreeVisitor");

        // InspectContractAsync must complete promptly — separate semaphore
        Result<TopContractEntry?> inspectResult = await service.InspectContractAsync(
            Address.Zero, header, testCt).WaitAsync(TimeSpan.FromSeconds(3), testCt);

        // Account not found → success with null (not a semaphore error)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inspectResult.IsSuccess, Is.True, "InspectContractAsync must not be blocked by scan semaphore");
            Assert.That(inspectResult.Data, Is.Null, "No storage found → null result");
        }

        // Release scan and clean up
        releaseScan.Set();
        await scanTask.WaitAsync(TimeSpan.FromSeconds(3), testCt);
    }
}
