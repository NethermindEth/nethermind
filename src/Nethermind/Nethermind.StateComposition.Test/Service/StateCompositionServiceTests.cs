// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Service;
using Nethermind.StateComposition.Snapshots;
using Nethermind.StateComposition.Test.Helpers;
using Nethermind.StateComposition.Visitors;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Service;

[TestFixture]
public class StateCompositionServiceTests
{
    private static IStateCompositionConfig CreateValidConfig() => TestDataBuilders.CreateTestConfig();

    // Build a realistic DI container using the canonical PseudoNethermindModule +
    // TestEnvironmentModule pair (see .agents/rules/test-infrastructure.md) so
    // IBlockTree and IWorldStateManager come from production wiring rather than
    // hand-rolled substitutes. IStateReader is overridden with a substitute
    // because these unit tests must inject behavior into RunTreeVisitor to
    // exercise the service's semaphore/cancellation semantics.
    private static IContainer BuildContainer(IStateReader stateReaderOverride)
    {
        ChainSpec spec = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance)
            .LoadEmbeddedOrFromFile("chainspec/foundation.json");
        spec.Bootnodes = [];

        ConfigProvider configProvider = new();

        return new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(spec, configProvider, LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, nameof(StateCompositionServiceTests)))
            .AddSingleton(stateReaderOverride)
            .Build();
    }

    private sealed class Harness(
        IContainer container,
        StateCompositionService service,
        StateCompositionStateHolder stateHolder) : IAsyncDisposable
    {
        public StateCompositionService Service { get; } = service;
        public StateCompositionStateHolder StateHolder { get; } = stateHolder;

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await container.DisposeAsync();
        }
    }

    private static Harness CreateHarness(IStateReader stateReader)
    {
        IContainer container = BuildContainer(stateReader);
        StateCompositionStateHolder stateHolder = new();
        StateCompositionSnapshotStore snapshotStore = new(new MemDb(), LimboLogs.Instance);

        StateCompositionService service = new(
            container.Resolve<IStateReader>(),
            container.Resolve<IWorldStateManager>(),
            container.Resolve<IBlockTree>(),
            stateHolder,
            snapshotStore,
            CreateValidConfig(),
            LimboLogs.Instance);

        return new Harness(container, service, stateHolder);
    }

    [Test]
    public async Task AnalyzeAsync_ReturnsStats_AndUpdatesStateHolder()
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        await using Harness harness = CreateHarness(stateReader);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        Result<StateCompositionStats> result = await harness.Service.AnalyzeAsync(header, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(harness.StateHolder.HasScanBaseline, Is.True);
            Assert.That(harness.StateHolder.LastScanMetadata.IsComplete, Is.True);
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

        await using Harness harness = CreateHarness(stateReader);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        // Start first scan — blocks inside RunTreeVisitor
        Task<Result<StateCompositionStats>> firstScan = harness.Service.AnalyzeAsync(header, CancellationToken.None);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True, "First scan did not enter RunTreeVisitor");

        // Second scan should return error immediately (fail-fast semaphore)
        Result<StateCompositionStats> second = await harness.Service.AnalyzeAsync(header, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.IsError, Is.True);
            Assert.That(second.Error, Does.Contain("already in progress"));
        }

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

        await using Harness harness = CreateHarness(stateReader);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        Task<Result<TopContractEntry?>> firstInspect =
            harness.Service.InspectContractAsync(Address.Zero, header, CancellationToken.None);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);

        Result<TopContractEntry?> second =
            await harness.Service.InspectContractAsync(Address.Zero, header, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.IsError, Is.True);
            Assert.That(second.Error, Does.Contain("inspection already in progress"));
        }

        blocker.SetResult();
        await firstInspect;
    }

    /// <summary>
    /// When the CancellationToken passed to AnalyzeAsync is canceled while the
    /// visitor is in-flight, the task must complete (not hang) and must NOT mark
    /// the baseline as initialized.
    ///
    /// The mock visitor throws OperationCanceledException when the scan token is
    /// canceled — this is the cooperative cancellation contract any real visitor
    /// must honor. The service propagates the exception; no Result.Success is produced.
    /// </summary>
    [Test]
    [CancelAfter(5_000)]
    public async Task AnalyzeAsync_CancelledMidScan_CompletesWithoutHangAndNoInitialization(CancellationToken testCt)
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        ManualResetEventSlim visitorEntered = new(false);
        using CancellationTokenSource cts = new();

        // cts is captured by reference; cts.Token.WaitHandle is the live handle that
        // await cts.CancelAsync() below signals, so the mock unblocks cooperatively.
        stateReader.WhenForAnyArgs(x =>
            x.RunTreeVisitor<StateCompositionContext>(null!, null))
            .Do(_ =>
            {
                visitorEntered.Set();
                cts.Token.WaitHandle.WaitOne();
                cts.Token.ThrowIfCancellationRequested();
            });

        await using Harness harness = CreateHarness(stateReader);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        Task<Result<StateCompositionStats>> scanTask = harness.Service.AnalyzeAsync(header, cts.Token);

        Assert.That(visitorEntered.Wait(TimeSpan.FromSeconds(3), testCt), Is.True, "Visitor did not enter RunTreeVisitor");

        await cts.CancelAsync();

        try
        {
            await scanTask.WaitAsync(TimeSpan.FromSeconds(3), testCt);
            // If we reach here the task returned a Result rather than throwing —
            // that's also acceptable as long as HasScanBaseline is false.
        }
        catch (OperationCanceledException) { }

        // Baseline must NOT have been installed — partial scan must not corrupt state
        Assert.That(harness.StateHolder.HasScanBaseline, Is.False,
            "Baseline must not be marked complete after mid-scan cancellation");
    }

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
                releaseScan.Wait(testCt);
            });

        // InspectContractAsync will call TryGetAccount first — return null account so it exits early
        stateReader.TryGetAccount(null!, null!, out Arg.Any<AccountStruct>())
            .ReturnsForAnyArgs(false);

        await using Harness harness = CreateHarness(stateReader);

        BlockHeader header = Build.A.BlockHeader.TestObject;

        Task<Result<StateCompositionStats>> scanTask = harness.Service.AnalyzeAsync(header, testCt);
        Assert.That(scanEntered.Wait(TimeSpan.FromSeconds(3), testCt), Is.True, "Scan did not enter RunTreeVisitor");

        Result<TopContractEntry?> inspectResult = await harness.Service.InspectContractAsync(
            Address.Zero, header, testCt).WaitAsync(TimeSpan.FromSeconds(3), testCt);

        // Account not found → success with null (not a semaphore error)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inspectResult.IsSuccess, Is.True, "InspectContractAsync must not be blocked by scan semaphore");
            Assert.That(inspectResult.Data, Is.Null, "No storage found → null result");
        }

        releaseScan.Set();
        await scanTask.WaitAsync(TimeSpan.FromSeconds(3), testCt);
    }

    [Test]
    public async Task CancelScan_ReturnsFalse_WhenNoScanActive()
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        await using Harness harness = CreateHarness(stateReader);

        Assert.That(harness.Service.CancelScan(), Is.False);
    }

    [Test]
    [CancelAfter(5_000)]
    public async Task CancelScan_ReturnsTrue_WhenScanActive(CancellationToken testCt)
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        ManualResetEventSlim scanEntered = new(false);
        ManualResetEventSlim releaseScan = new(false);

        stateReader.WhenForAnyArgs(x =>
            x.RunTreeVisitor<StateCompositionContext>(null!, null))
            .Do(_ =>
            {
                scanEntered.Set();
                releaseScan.Wait(testCt);
            });

        await using Harness harness = CreateHarness(stateReader);

        BlockHeader header = Build.A.BlockHeader.TestObject;
        Task<Result<StateCompositionStats>> scanTask = harness.Service.AnalyzeAsync(header, testCt);
        Assert.That(scanEntered.Wait(TimeSpan.FromSeconds(3), testCt), Is.True, "Scan did not enter RunTreeVisitor");

        Assert.That(harness.Service.CancelScan(), Is.True);

        releaseScan.Set();
        try { await scanTask.WaitAsync(TimeSpan.FromSeconds(3), testCt); }
        catch (OperationCanceledException) { }
    }
}
