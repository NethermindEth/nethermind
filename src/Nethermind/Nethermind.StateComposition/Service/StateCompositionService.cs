// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.ServiceStopper;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Diff;
using Nethermind.StateComposition.Visitors;
using Nethermind.StateComposition.Snapshots;

namespace Nethermind.StateComposition.Service;

internal sealed partial class StateCompositionService : IStoppableService, IDisposable
{
    private const long MinMemoryBudgetBytes = 1_048_576L; // 1 MiB
    private const int MaxScanParallelism = 16;
    private const int MaxTopNContracts = 10_000;

    private readonly IStateReader _stateReader;
    private readonly IWorldStateManager _worldStateManager;
    private readonly IBlockTree _blockTree;
    private readonly StateCompositionStateHolder _stateHolder;
    private readonly StateCompositionSnapshotStore _snapshotStore;
    private readonly IStateCompositionConfig _config;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly SemaphoreSlim _inspectLock = new(1, 1);
    // Non-blocking coalescing lock for per-block incremental diffs. Only used
    // synchronously inside Task.Run, so a managed Lock is a better fit than
    // SemaphoreSlim — removes the IDisposable/kernel-object overhead.
    private readonly Lock _diffLock = new();

    private readonly TrieDiffWalker _diffWalker;

    // Written from the scan task, read by CancelScan() from arbitrary threads.
    // volatile guarantees the read sees the most recent write without a lock.
    private volatile CancellationTokenSource? _currentScanCts;

    // Set by StopAsync before teardown so diff tasks queued between `-=` and
    // disposal short-circuit instead of racing IWorldStateManager/IDb.
    private volatile bool _shuttingDown;

    public StateCompositionService(
        IStateReader stateReader,
        IWorldStateManager worldStateManager,
        IBlockTree blockTree,
        StateCompositionStateHolder stateHolder,
        StateCompositionSnapshotStore snapshotStore,
        IStateCompositionConfig config,
        ILogManager logManager)
    {
        _stateReader = stateReader;
        _worldStateManager = worldStateManager;
        _blockTree = blockTree;
        _stateHolder = stateHolder;
        _snapshotStore = snapshotStore;
        _config = config;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<StateCompositionService>();
        _diffWalker = new TrieDiffWalker(config.TrackDepthIncrementally);

        _blockTree.NewHeadBlock += OnNewHeadBlock;
    }

    /// <summary>
    /// Run a full trie scan. The plugin has exactly two legal callers — any third
    /// caller collapses the single-operating-mode invariant, so a reviewer adding
    /// one must delete this comment as a tripwire:
    /// <list type="bullet">
    /// <item><description>Plugin bootstrap: <see cref="StateCompositionPlugin.Init"/>
    /// schedules a background scan when no persisted snapshot is available.</description></item>
    /// <item><description>Incremental recovery: <see cref="ScheduleBaselineRescan"/>
    /// from the <see cref="MissingTrieNodeException"/> handler in
    /// <see cref="RunIncrementalDiff"/>.</description></item>
    /// </list>
    /// Fail-fast / queued via <see cref="_scanLock"/> so back-to-back triggers collapse into one scan.
    /// </summary>
    public async Task<Result<StateCompositionStats>> AnalyzeAsync(BlockHeader header, CancellationToken ct)
    {
        TimeSpan queueTimeout = _config.ScanQueueTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(_config.ScanQueueTimeoutSeconds)
            : TimeSpan.Zero;
        bool acquired = await _scanLock.WaitAsync(queueTimeout, ct).ConfigureAwait(false);
        if (!acquired)
            return Result<StateCompositionStats>.Fail("Scan already in progress");

        try
        {
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _currentScanCts = linkedCts;

            Stopwatch sw = Stopwatch.StartNew();

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: starting full scan at block {header.Number}, root {header.StateRoot}");

            (int topN, int parallelism, long memoryBudget) = ResolveScanOptions();

            int codeSizeLookup(ValueHash256 hash) => _stateReader.GetCode(hash)?.Length ?? 0;

            using StateCompositionVisitor visitor = new(
                _logManager,
                topN,
                _config.ExcludeStorage,
                codeSizeLookup,
                linkedCts.Token);

            VisitingOptions options = new()
            {
                MaxDegreeOfParallelism = parallelism,
                FullScanMemoryBudget = memoryBudget,
            };

            using PeriodicTimer progressTimer = StartProgressLogging(sw, visitor, linkedCts.Token);

            await Task.Run(() =>
                _stateReader.RunTreeVisitor(visitor, header, options), linkedCts.Token).ConfigureAwait(false);

            StateCompositionStats stats = visitor.GetStats(header.Number, header.StateRoot);
            TrieDepthDistribution dist = visitor.GetTrieDistribution();
            bool scanComplete = !visitor.MissingNodesObserved;

            PublishScanResults(stats, dist, header, sw, scanComplete);

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: scan {(scanComplete ? "completed" : "finished with MISSING NODES")} " +
                             $"in {sw.Elapsed}. Accounts={stats.AccountsTotal}, Contracts={stats.ContractsTotal}, " +
                             $"StorageSlots={stats.StorageSlotsTotal}");

            return Result<StateCompositionStats>.Success(stats);
        }
        finally
        {
            _currentScanCts = null;
            _scanLock.Release();
        }
    }

    public async Task<Result<TopContractEntry?>> InspectContractAsync(Address address, BlockHeader header, CancellationToken ct)
    {
        bool acquired = await _inspectLock.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
        if (!acquired)
            return Result<TopContractEntry?>.Fail("Contract inspection already in progress");

        try
        {
            if (!_stateReader.TryGetAccount(header, address, out AccountStruct account) || !account.HasStorage)
                return Result<TopContractEntry?>.Success(null);

            ValueHash256 accountHash = ValueKeccak.Compute(address.Bytes);
            ValueHash256 targetStorageRoot = account.StorageRoot;

            // Debug-level: inspectContract is RPC-polled (e.g. Grafana top-10 every minute)
            // and logging at Info would spam the node log with one line per request.
            if (_logger.IsDebug)
                _logger.Debug($"StateComposition: inspecting contract {address}, storageRoot={targetStorageRoot}");

            using SingleContractVisitor visitor = new(_logManager, targetStorageRoot, ct);

            VisitingOptions options = new()
            {
                MaxDegreeOfParallelism = 1,
                FullScanMemoryBudget = _config.ScanMemoryBudgetBytes,
            };

            await Task.Run(() =>
                _stateReader.RunTreeVisitor(visitor, header, options), ct).ConfigureAwait(false);

            return Result<TopContractEntry?>.Success(visitor.GetResult(accountHash, targetStorageRoot));
        }
        finally
        {
            _inspectLock.Release();
        }
    }

    /// <summary>
    /// Cancels the currently running scan, if any.
    /// </summary>
    /// <returns><c>true</c> if a scan was active and a cancellation signal was issued; <c>false</c> if no scan was running.</returns>
    public bool CancelScan()
    {
        // Capture to local variable to prevent TOCTOU race.
        // The CTS may be disposed between our read and Cancel() call
        // if the scan completes concurrently — catch and ignore.
        CancellationTokenSource? cts = _currentScanCts;
        if (cts is null) return false;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException) { }
        return true;
    }

    private (int topN, int parallelism, long memoryBudget) ResolveScanOptions()
    {
        int topN = ClampWithWarn(_config.TopNContracts, 1, MaxTopNContracts, nameof(_config.TopNContracts));
        int parallelism = ClampWithWarn(_config.ScanParallelism, 1, MaxScanParallelism, nameof(_config.ScanParallelism));
        long memoryBudget = Math.Max(MinMemoryBudgetBytes, _config.ScanMemoryBudgetBytes);
        if (_config.ScanMemoryBudgetBytes < MinMemoryBudgetBytes && _logger.IsWarn)
            _logger.Warn($"StateComposition: ScanMemoryBudgetBytes={_config.ScanMemoryBudgetBytes} below minimum ({MinMemoryBudgetBytes} = 1 MiB); clamped to {memoryBudget}");

        return (topN, parallelism, memoryBudget);
    }

    private int ClampWithWarn(int value, int min, int max, string name)
    {
        int clamped = Math.Clamp(value, min, max);
        if (value != clamped && _logger.IsWarn)
            _logger.Warn($"StateComposition: {name}={value} out of range [{min}, {max}]; clamped to {clamped}");
        return clamped;
    }

    private PeriodicTimer StartProgressLogging(Stopwatch sw, StateCompositionVisitor visitor, CancellationToken token)
    {
        PeriodicTimer progressTimer = new(TimeSpan.FromSeconds(8));
        _ = Task.Run(async () =>
        {
            try
            {
                while (await progressTimer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    if (_logger.IsInfo)
                    {
                        double elapsed = sw.Elapsed.TotalSeconds;
                        ScanSnapshot snap = visitor.GetSnapshot();
                        _logger.Info(
                            $"StateComposition: {elapsed:F1}s | " +
                            $"accounts: {ScanSnapshot.Fmt(snap.Accounts)} ({ScanSnapshot.Fmt(snap.Accounts / elapsed)}/s) | " +
                            $"contracts: {ScanSnapshot.Fmt(snap.Contracts)} (storage: {ScanSnapshot.Fmt(snap.ContractsWithStorage)}) | " +
                            $"slots: {ScanSnapshot.Fmt(snap.StorageSlots)} ({ScanSnapshot.Fmt(snap.StorageSlots / elapsed)}/s) | " +
                            $"nodes: {ScanSnapshot.Fmt(snap.Nodes)} ({ScanSnapshot.Fmt(snap.Nodes / elapsed)}/s) | " +
                            $"data: {snap.Bytes.SizeToString(useSi: true)}");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
        return progressTimer;
    }

    private void PublishScanResults(StateCompositionStats stats, TrieDepthDistribution dist, BlockHeader header, Stopwatch sw, bool isComplete)
    {
        CumulativeTrieStats cumulativeBaseline = CumulativeTrieStats.FromScanStats(stats);

        // Seed and publish under _diffLock so a concurrent incremental diff cannot
        // clobber the fresh baseline's metrics or read a torn 9×16 depth table.
        // Lock order _scanLock → _diffLock is held; the diff path only takes
        // _diffLock, so the nested acquire is deadlock-free.
        lock (_diffLock)
        {
            _stateHolder.PublishScanBaseline(
                stats, dist, header.Number, header.StateRoot!, sw.Elapsed, isComplete,
                cumulativeBaseline,
                slotCountByAddress: stats.SlotCountByAddress,
                codeHashRefcounts: stats.CodeHashRefcounts,
                codeHashSizes: stats.CodeHashSizes);

            Metrics.UpdateDepthDistribution(_stateHolder.CurrentDepthStats);
            Metrics.UpdateFromCumulativeStats(cumulativeBaseline);
            Metrics.StateCompScanDurationSeconds = sw.Elapsed.TotalSeconds;
            Metrics.StateCompScanBlock = header.Number;
            Metrics.StateCompIncrementalBlock = header.Number;
            Metrics.StateCompDiffsSinceBaseline = 0;
            Metrics.StateCompScansCompleted++;
        }
    }

    /// <summary>
    /// Persist the current incremental state for the given head. Single funnel
    /// for every snapshot write — interval writes, scan completion, and the
    /// graceful-shutdown flush all go through here so behavior cannot drift.
    /// </summary>
    private void WriteSnapshotForHead(CumulativeTrieStats stats, long blockNumber, Hash256 stateRoot)
    {
        if (!_config.PersistSnapshots) return;
        _snapshotStore.WriteSnapshot(_stateHolder.BuildSnapshot(stats, blockNumber, stateRoot));
    }

    /// <summary>
    /// Graceful shutdown hook invoked by <see cref="IServiceStopper"/> before the
    /// snapshot RocksDB is disposed. Unsubscribes from <see cref="IBlockTree.NewHeadBlock"/>
    /// to stop dispatching diffs, cancels any in-flight scan, then drains the diff
    /// path via <see cref="_diffLock"/> so the snapshot capture and the RLP encoder
    /// observe a consistent view of the holder. Writes the latest incremental
    /// state so the next startup can resume without a full rescan.
    /// </summary>
    public async Task StopAsync()
    {
        _shuttingDown = true;

        // Stop dispatching new diffs first. Delegate `-=` is idempotent, so this
        // is safe even if Dispose later runs the same line.
        _blockTree.NewHeadBlock -= OnNewHeadBlock;

        CancelScan();

        // Bounded wait: if the in-flight scan ignores cancellation we would otherwise
        // stall node shutdown indefinitely. Dropping the snapshot flush on timeout is
        // preferable to a hang — the next startup falls back to a bootstrap scan.
        bool acquired = await _scanLock.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (!acquired)
        {
            if (_logger.IsWarn)
                _logger.Warn("StateComposition: shutdown flush skipped — scan did not release within 10s");
            return;
        }

        try
        {
            lock (_diffLock)
            {
                if (!_stateHolder.TryGetShutdownSnapshot(out Hash256 stateRoot, out long blockNumber, out CumulativeTrieStats stats))
                    return;

                if (_logger.IsInfo)
                    _logger.Info($"StateComposition: shutdown flush — writing snapshot at block {blockNumber}");

                WriteSnapshotForHead(stats, blockNumber, stateRoot);
            }
        }
        finally
        {
            _scanLock.Release();
        }
    }

    public string Description => "StateComposition";

    public void Dispose()
    {
        _blockTree.NewHeadBlock -= OnNewHeadBlock;
        _scanLock.Dispose();
        _inspectLock.Dispose();
        // _diffLock is a managed Lock — no Dispose required.
    }
}
