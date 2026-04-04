// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.StateComposition;

/// <summary>
/// Orchestrates state composition analysis using <see cref="StateCompositionVisitor"/>
/// and <see cref="IStateReader"/> for trie traversal.
/// </summary>
public sealed class StateCompositionService : IStateCompositionService, IDisposable
{
    private readonly IStateReader _stateReader;
    private readonly IStateCompositionStateHolder _stateHolder;
    private readonly IStateCompositionConfig _config;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly SemaphoreSlim _inspectLock = new(1, 1);

    private CancellationTokenSource? _currentScanCts;

    public StateCompositionService(
        IStateReader stateReader,
        IStateCompositionStateHolder stateHolder,
        IStateCompositionConfig config,
        ILogManager logManager)
    {
        _stateReader = stateReader;
        _stateHolder = stateHolder;
        _config = config;
        _logManager = logManager;
        _logger = logManager.GetClassLogger();

        ValidateConfig(config);
    }

    private static void ValidateConfig(IStateCompositionConfig config)
    {
        if (config.ScanParallelism <= 0)
            throw new ArgumentException("ScanParallelism must be positive", nameof(config));
        if (config.ScanMemoryBudget <= 0)
            throw new ArgumentException("ScanMemoryBudget must be positive", nameof(config));
        if (config.ScanQueueTimeoutSeconds <= 0)
            throw new ArgumentException("ScanQueueTimeoutSeconds must be positive", nameof(config));
        if (config.TopNContracts <= 0)
            throw new ArgumentException("TopNContracts must be positive", nameof(config));
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

            using StateCompositionVisitor visitor = new(
                _logManager, _config.TopNContracts, _config.ExcludeStorage, linkedCts.Token);

            VisitingOptions options = new()
            {
                MaxDegreeOfParallelism = _config.ScanParallelism,
                FullScanMemoryBudget = _config.ScanMemoryBudget,
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

            _stateHolder.StoreScan(header.Number, header.StateRoot!, sw.Elapsed, stats, dist);

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

    public Task<Result<TrieDepthDistribution>> GetTrieDistributionAsync(long? blockNumber)
    {
        ScanCacheEntry? entry = _stateHolder.GetScan(blockNumber);
        if (entry is not null)
            return Task.FromResult(Result<TrieDepthDistribution>.Success(entry.Value.Distribution));

        string msg = blockNumber is not null
            ? $"No cached scan for block {blockNumber}. Run statecomp_getStats first."
            : "No cached data available. Run statecomp_getStats() first to trigger a scan.";
        return Task.FromResult(Result<TrieDepthDistribution>.Fail(msg));
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

    public void CancelScan()
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
    /// Releases the semaphore resources used for rate limiting.
    /// </summary>
    public void Dispose()
    {
        _scanLock.Dispose();
        _inspectLock.Dispose();
    }
}
