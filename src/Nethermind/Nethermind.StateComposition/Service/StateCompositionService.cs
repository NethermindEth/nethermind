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
using Nethermind.Trie.Pruning;

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Visitors;
using Nethermind.StateComposition.Diff;
using Nethermind.StateComposition.Snapshots;

namespace Nethermind.StateComposition.Service;

/// <summary>
/// Orchestrates state composition analysis using <see cref="StateCompositionVisitor"/>
/// and <see cref="IStateReader"/> for trie traversal.
/// </summary>
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

    private CancellationTokenSource? _currentScanCts;

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

            using StateCompositionVisitor visitor = new(
                _logManager, topN, _config.ExcludeStorage, linkedCts.Token);

            VisitingOptions options = new()
            {
                MaxDegreeOfParallelism = parallelism,
                FullScanMemoryBudget = memoryBudget,
            };

            using PeriodicTimer progressTimer = StartProgressLogging(sw, visitor);

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

    /// <summary>
    /// Clamp config-driven scan options to their supported ranges, warning on clamp.
    /// </summary>
    private (int topN, int parallelism, long memoryBudget) ResolveScanOptions()
    {
        int topN = Math.Clamp(_config.TopNContracts, 1, MaxTopNContracts);
        if (_config.TopNContracts != topN && _logger.IsWarn)
            _logger.Warn($"StateComposition: TopNContracts={_config.TopNContracts} out of range [1, {MaxTopNContracts}]; clamped to {topN}");

        int parallelism = Math.Clamp(_config.ScanParallelism, 1, MaxScanParallelism);
        if (_config.ScanParallelism != parallelism && _logger.IsWarn)
            _logger.Warn($"StateComposition: ScanParallelism={_config.ScanParallelism} out of range [1, {MaxScanParallelism}]; clamped to {parallelism}");

        long memoryBudget = Math.Max(MinMemoryBudgetBytes, _config.ScanMemoryBudgetBytes);
        if (_config.ScanMemoryBudgetBytes < MinMemoryBudgetBytes && _logger.IsWarn)
            _logger.Warn($"StateComposition: ScanMemoryBudgetBytes={_config.ScanMemoryBudgetBytes} below minimum ({MinMemoryBudgetBytes} = 1 MiB); clamped to {memoryBudget}");

        return (topN, parallelism, memoryBudget);
    }

    /// <summary>
    /// Fire an 8-second periodic progress logger. The returned timer owns the fire-and-forget
    /// task — disposing it ends the log loop (via ObjectDisposedException on WaitForNextTickAsync).
    /// </summary>
    private PeriodicTimer StartProgressLogging(Stopwatch sw, StateCompositionVisitor visitor)
    {
        PeriodicTimer progressTimer = new(TimeSpan.FromSeconds(8));
        _ = Task.Run(async () =>
        {
            try
            {
                while (await progressTimer.WaitForNextTickAsync(CancellationToken.None).ConfigureAwait(false))
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
            catch (ObjectDisposedException) { }
        }, CancellationToken.None);
        return progressTimer;
    }

    /// <summary>
    /// Write the finalized baseline into the state holder, publish metrics, and persist a snapshot.
    /// </summary>
    private void PublishScanResults(StateCompositionStats stats, TrieDepthDistribution dist, BlockHeader header, Stopwatch sw)
    {
        _stateHolder.SetBaseline(stats, dist);
        _stateHolder.MarkScanCompleted(header.Number, header.StateRoot!, sw.Elapsed);
        CumulativeSizeStats cumulativeBaseline = CumulativeSizeStats.FromScanStats(stats);
        _stateHolder.InitializeIncremental(cumulativeBaseline, header.Number, header.StateRoot!, dist);

        // ContractsWithStorage and EmptyAccounts are now part of CumulativeSizeStats and
        // are wired through UpdateFromCumulativeStats — incremental diffs keep them current.
        Metrics.UpdateFromCumulativeStats(cumulativeBaseline);
        Metrics.UpdateFromDistribution(dist);
        Metrics.UpdateFromDepthStats(_stateHolder.CurrentDepthStats);
        Metrics.StateCompScanDurationSeconds = sw.Elapsed.TotalSeconds;
        Metrics.StateCompScanBlock = header.Number;
        Metrics.StateCompIncrementalBlock = header.Number;
        Metrics.StateCompDiffsSinceBaseline = 0;
        Metrics.StateCompScansCompleted++;

        if (_config.PersistSnapshots)
            _snapshotStore.WriteSnapshot(new StateCompositionSnapshot(
                cumulativeBaseline, header.Number, header.StateRoot!, 0, header.Number,
                // CurrentDepthStats already returns a clone under lock — no extra Clone() needed.
                _stateHolder.CurrentDepthStats));
    }

    public void Dispose()
    {
        _blockTree.NewHeadBlock -= OnNewHeadBlock;
        _scanLock.Dispose();
        _inspectLock.Dispose();
        // _diffLock is a managed Lock — no Dispose required.
    }
}
