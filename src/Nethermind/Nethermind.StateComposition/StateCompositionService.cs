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

namespace Nethermind.StateComposition;

/// <summary>
/// Orchestrates state composition analysis using <see cref="StateCompositionVisitor"/>
/// and <see cref="IStateReader"/> for trie traversal.
/// </summary>
public class StateCompositionService : IDisposable
{
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
    private readonly SemaphoreSlim _diffLock = new(1, 1);

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
        // Fail-fast — if semaphore not immediately available, reject.
        // Does not block the calling thread or thread pool.
        bool acquired = await _scanLock.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
        if (!acquired)
            return Result<StateCompositionStats>.Fail("Scan already in progress");

        try
        {
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _currentScanCts = linkedCts;

            Stopwatch sw = Stopwatch.StartNew();

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: starting full scan at block {header.Number}, root {header.StateRoot}");

            int topN = Math.Max(1, _config.TopNContracts);
            if (_config.TopNContracts <= 0 && _logger.IsWarn)
                _logger.Warn($"StateComposition: TopNContracts={_config.TopNContracts} is invalid; clamped to {topN}");

            int parallelism = Math.Max(1, _config.ScanParallelism);
            if (_config.ScanParallelism <= 0 && _logger.IsWarn)
                _logger.Warn($"StateComposition: ScanParallelism={_config.ScanParallelism} is invalid; clamped to {parallelism}");

            long memoryBudget = Math.Max(1, _config.ScanMemoryBudget);
            if (_config.ScanMemoryBudget <= 0 && _logger.IsWarn)
                _logger.Warn($"StateComposition: ScanMemoryBudget={_config.ScanMemoryBudget} is invalid; clamped to {memoryBudget}");

            using StateCompositionVisitor visitor = new(
                _logManager, topN, _config.ExcludeStorage, linkedCts.Token);

            VisitingOptions options = new()
            {
                MaxDegreeOfParallelism = parallelism,
                FullScanMemoryBudget = memoryBudget,
            };

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
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }, CancellationToken.None);

            try
            {
                await Task.Run(() =>
                    _stateReader.RunTreeVisitor(visitor, header, options), linkedCts.Token).ConfigureAwait(false);
            }
            finally
            {
                progressTimer.Dispose();
            }

            StateCompositionStats stats = visitor.GetStats(header.Number, header.StateRoot);
            TrieDepthDistribution dist = visitor.GetTrieDistribution();

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
                    _stateHolder.CurrentDepthStats.Clone()));

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

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: inspecting contract {address}, storageRoot={targetStorageRoot}");

            using SingleContractVisitor visitor = new(_logManager, targetStorageRoot, ct);

            VisitingOptions options = new()
            {
                MaxDegreeOfParallelism = 1,
                FullScanMemoryBudget = _config.ScanMemoryBudget,
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

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        Hash256? lastRoot = _stateHolder.LastProcessedStateRoot;
        if (lastRoot is null) return; // No baseline yet

        Hash256? newRoot = e.Block.Header.StateRoot;
        if (newRoot is null || newRoot == lastRoot) return; // No state change

        _ = Task.Run(() =>
        {
            // Non-blocking: if another diff is running, skip — it will pick up the latest head.
            if (!_diffLock.Wait(0)) return;
            try
            {
                // Re-read inside lock for coalescing: diff from real latest to current head.
                Hash256? prevRoot = _stateHolder.LastProcessedStateRoot;
                Block? head = _blockTree.Head;
                if (head?.Header.StateRoot is null || head.Header.StateRoot == prevRoot) return;

                using IReadOnlyTrieStore readOnlyStore = _worldStateManager.CreateReadOnlyTrieStore();
                IScopedTrieStore resolver = readOnlyStore.GetTrieStore(null);
                TrieDiffWalker walker = new(resolver, _config.TrackDepthIncrementally);

                TrieDiff diff = walker.ComputeDiff(prevRoot, head.Header.StateRoot);
                CumulativeSizeStats updated = _stateHolder.IncrementalStats!.Value.ApplyDiff(diff);
                _stateHolder.UpdateIncremental(updated, head.Number, head.Header.StateRoot, diff.DepthDelta);

                Metrics.UpdateFromCumulativeStats(updated);
                // Skip the 149-setter publish when the depth distribution did not change.
                // Gauges retain their last published value, which is correct — nothing changed.
                if (_config.TrackDepthIncrementally && diff.DepthDelta?.IsEmpty() != true)
                    Metrics.UpdateFromDepthStats(_stateHolder.CurrentDepthStats);
                Metrics.StateCompIncrementalBlock = head.Number;
                Metrics.StateCompDiffsSinceBaseline = _stateHolder.DiffsSinceBaseline;
                Metrics.StateCompDiffsApplied++;

                if (_config.PersistSnapshots && head.Number % _config.SnapshotInterval == 0)
                {
                    _snapshotStore.WriteSnapshot(new StateCompositionSnapshot(
                        updated, head.Number, head.Header.StateRoot,
                        _stateHolder.DiffsSinceBaseline,
                        _stateHolder.LastScanMetadata?.BlockNumber ?? 0,
                        _stateHolder.CurrentDepthStats.Clone()));

                    // Prune stale snapshot entries beyond the configured retention window.
                    int blocksToKeep = _config.SnapshotBlocksToKeep;
                    if (blocksToKeep > 0)
                    {
                        long deleteAt = head.Number - blocksToKeep;
                        if (deleteAt > 0)
                            _snapshotStore.DeleteSnapshot(deleteAt);
                    }
                }

                if (_logger.IsDebug)
                    _logger.Debug($"StateComposition: incremental diff applied at block {head.Number}, " +
                                  $"accounts={updated.AccountsTotal}, slots={updated.StorageSlotsTotal}");
            }
            catch (Exception ex)
            {
                Metrics.StateCompDiffErrors++;
                if (_logger.IsError)
                    _logger.Error("StateComposition: failed to compute incremental diff", ex);
            }
            finally
            {
                _diffLock.Release();
            }
        });
    }

    public void Dispose()
    {
        _blockTree.NewHeadBlock -= OnNewHeadBlock;
        _scanLock.Dispose();
        _inspectLock.Dispose();
        _diffLock.Dispose();
    }
}
