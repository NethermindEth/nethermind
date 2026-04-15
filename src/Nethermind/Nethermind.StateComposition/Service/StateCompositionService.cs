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
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Visitors;
using Nethermind.StateComposition.Snapshots;

namespace Nethermind.StateComposition.Service;

internal partial class StateCompositionService : IDisposable
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

    // Written from the scan task, read by CancelScan() from arbitrary threads.
    // volatile guarantees the read sees the most recent write without a lock.
    private volatile CancellationTokenSource? _currentScanCts;

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

        _blockTree.NewHeadBlock += OnNewHeadBlock;
    }

    /// <summary>
    /// Run a full trie scan. Legal call sites — do not add more:
    /// <list type="bullet">
    /// <item><description>Explicit RPC: <c>statecomp_getStats</c> (operator-initiated).</description></item>
    /// <item><description>Error recovery: <see cref="ScheduleBaselineRescan"/> after a
    /// <see cref="Nethermind.Trie.MissingTrieNodeException"/> invalidates the incremental baseline.</description></item>
    /// </list>
    /// Fail-fast / queued via <see cref="_scanLock"/> so back-to-back triggers collapse into one scan.
    /// </summary>
    public async Task<Result<StateCompositionStats>> AnalyzeAsync(BlockHeader header, CancellationToken ct)
    {
        // If the semaphore is busy, either fail-fast (timeout <= 0) or wait briefly.
        // Configurable via ScanQueueTimeoutSeconds — 0 keeps the original fail-fast behavior.
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

            // Close over _stateReader so the visitor can fetch bytecode by codeHash
            // for the CodeBytesTotal metric. Dedup happens inside the visitor — each
            // unique codeHash triggers exactly one GetCode call across all workers.
            Func<ValueHash256, int> codeSizeLookup =
                hash => _stateReader.GetCode(hash)?.Length ?? 0;

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

            PublishScanResults(stats, dist, header, sw);

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: scan completed in {sw.Elapsed}. " +
                             $"Accounts={stats.AccountsTotal}, Contracts={stats.ContractsTotal}, " +
                             $"StorageSlots={stats.StorageSlotsTotal}");

            return Result<StateCompositionStats>.Success(stats);
        }
        finally
        {
            _currentScanCts = null;
            _scanLock.Release();
        }
    }

    public Result<TrieDepthDistribution> GetTrieDistribution()
    {
        if (_stateHolder.IsInitialized)
            return Result<TrieDepthDistribution>.Success(_stateHolder.CurrentDistribution);

        return Result<TrieDepthDistribution>.Fail(
            "No cached data available. Run statecomp_getStats() first to trigger a scan.");
    }

    public async Task<Result<TopContractEntry?>> InspectContractAsync(Address address, BlockHeader header, CancellationToken ct)
    {
        // Fail-fast semaphore to prevent concurrent heavy inspections.
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

    public virtual void CancelScan()
    {
        // Capture to local variable to prevent TOCTOU race.
        // The CTS may be disposed between our read and Cancel() call
        // if the scan completes concurrently — catch and ignore.
        CancellationTokenSource? cts = _currentScanCts;
        if (cts is null) return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Scan already completed and disposed the CTS
        }
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

    private void PublishScanResults(StateCompositionStats stats, TrieDepthDistribution dist, BlockHeader header, Stopwatch sw)
    {
        _stateHolder.SetBaseline(stats, dist);
        _stateHolder.MarkScanCompleted(header.Number, header.StateRoot!, sw.Elapsed);
        CumulativeSizeStats cumulativeBaseline = CumulativeSizeStats.FromScanStats(stats);
        _stateHolder.InitializeIncremental(
            cumulativeBaseline, header.Number, header.StateRoot!, dist,
            slotCountByAddress: stats.SlotCountByAddress,
            codeHashRefcounts: stats.CodeHashRefcounts,
            codeHashSizes: stats.CodeHashSizes);

        // ContractsWithStorage and EmptyAccounts are now part of CumulativeSizeStats and
        // are wired through UpdateFromCumulativeStats — incremental diffs keep them current.
        Metrics.UpdateFromCumulativeStats(cumulativeBaseline);
        Metrics.UpdateDepthDistribution(_stateHolder.CurrentDepthStats);
        Metrics.StateCompScanDurationSeconds = sw.Elapsed.TotalSeconds;
        Metrics.StateCompScanBlock = header.Number;
        Metrics.StateCompIncrementalBlock = header.Number;
        Metrics.StateCompDiffsSinceBaseline = 0;
        Metrics.StateCompScansCompleted++;

        if (_config.PersistSnapshots)
            _snapshotStore.WriteSnapshot(
                _stateHolder.BuildSnapshot(cumulativeBaseline, header.Number, header.StateRoot!));
    }

    public void Dispose()
    {
        _blockTree.NewHeadBlock -= OnNewHeadBlock;
        _scanLock.Dispose();
        _inspectLock.Dispose();
        // _diffLock is a managed Lock — no Dispose required.
    }
}
