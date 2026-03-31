// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    private long _lastScanCompletedTicks;

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
        if (config.ScanCooldownSeconds < 0)
            throw new ArgumentException("ScanCooldownSeconds must be non-negative", nameof(config));
    }

    public async Task<StateCompositionStats> AnalyzeAsync(BlockHeader header, CancellationToken ct)
    {
        // C-3: Fail-fast — if semaphore not immediately available, reject.
        // Does not block the calling thread or thread pool.
        bool acquired = await _scanLock.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
        if (!acquired)
            throw new StateCompositionException(
                "Scan already in progress. Use statecomp_getCachedStats() for last results.");

        try
        {
            // H-1: Cooldown check INSIDE critical section to prevent bypass via concurrent requests.
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastScanCompletedTicks);
            long cooldownMs = _config.ScanCooldownSeconds * 1000L;
            if (last > 0 && now - last < cooldownMs)
            {
                long remainingSeconds = (cooldownMs - (now - last)) / 1000;
                throw new StateCompositionException(
                    $"Scan cooldown active. Try again in {remainingSeconds} seconds.");
            }

            _stateHolder.MarkScanStarted();
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _currentScanCts = linkedCts;

            Stopwatch sw = Stopwatch.StartNew();

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: starting full scan at block {header.Number}, root {header.StateRoot}");

            using StateCompositionVisitor visitor = new(
                _logManager, linkedCts.Token, _config.TopNContracts, _config.ExcludeStorage);

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
                            _logger.Info($"StateComposition: scan in progress, elapsed {sw.Elapsed}");
                    }
                }
                catch (OperationCanceledException) { }
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

            Interlocked.Exchange(ref _lastScanCompletedTicks, Environment.TickCount64);

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: scan completed in {sw.Elapsed}. " +
                             $"Accounts={stats.AccountsTotal}, Contracts={stats.ContractsTotal}, " +
                             $"StorageSlots={stats.StorageSlotsTotal}");

            return stats;
        }
        finally
        {
            _currentScanCts = null;
            _scanLock.Release();
        }
    }

    public Task<TrieDepthDistribution> GetTrieDistributionAsync(BlockHeader header, CancellationToken ct)
    {
        if (_stateHolder.IsInitialized)
            return Task.FromResult(_stateHolder.CurrentDistribution);

        throw new StateCompositionException(
            "No cached distribution available. Run statecomp_getStats() first to trigger a scan.");
    }

    public async Task<TopContractEntry?> InspectContractAsync(Address address, BlockHeader header, CancellationToken ct)
    {
        // M-3: Fail-fast semaphore to prevent concurrent heavy inspections.
        bool acquired = await _inspectLock.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
        if (!acquired)
            throw new StateCompositionException(
                "Contract inspection already in progress. Try again later.");

        try
        {
            if (!_stateReader.TryGetAccount(header, address, out AccountStruct account))
                return null;

            if (!account.HasStorage)
                return null;

            ValueHash256 accountHash = ValueKeccak.Compute(address.Bytes);
            ValueHash256 targetStorageRoot = account.StorageRoot;

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: inspecting contract {address}, storageRoot={targetStorageRoot}");

            using SingleContractVisitor visitor = new(_logManager, ct, targetStorageRoot);

            VisitingOptions options = new()
            {
                MaxDegreeOfParallelism = 1,
                FullScanMemoryBudget = _config.ScanMemoryBudget,
            };

            await Task.Run(() =>
                _stateReader.RunTreeVisitor(visitor, header, options), ct).ConfigureAwait(false);

            return visitor.GetResult(accountHash, targetStorageRoot);
        }
        finally
        {
            _inspectLock.Release();
        }
    }

    public void CancelScan()
    {
        // H-2: Capture to local variable to prevent TOCTOU race.
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
