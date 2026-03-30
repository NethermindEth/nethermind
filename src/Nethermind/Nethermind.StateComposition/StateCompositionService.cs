// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.StateComposition;

/// <summary>
/// Orchestrates state composition analysis using <see cref="StateCompositionVisitor"/>
/// and <see cref="IStateReader"/> for trie traversal.
/// </summary>
public sealed class StateCompositionService : IStateCompositionService
{
    private readonly IStateReader _stateReader;
    private readonly IStateCompositionStateHolder _stateHolder;
    private readonly IStateCompositionConfig _config;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

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

    public async Task<StateCompositionStats> AnalyzeAsync(BlockHeader header, CancellationToken ct)
    {
        bool acquired = false;
        try
        {
            acquired = await _scanLock.WaitAsync(
                TimeSpan.FromSeconds(_config.ScanQueueTimeoutSeconds), ct).ConfigureAwait(false);

            if (!acquired)
                throw new InvalidOperationException(
                    "Scan already in progress. Use statecomp_getCachedStats() for last results.");

            _stateHolder.MarkScanStarted();
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _currentScanCts = linkedCts;

            Stopwatch sw = Stopwatch.StartNew();

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: starting full scan at block {header.Number}, root {header.StateRoot}");

            using StateCompositionVisitor visitor = new(
                new OneLoggerLogManager(_logger), linkedCts.Token, _config.TopNContracts);

            VisitingOptions options = new()
            {
                MaxDegreeOfParallelism = _config.ScanParallelism,
                FullScanMemoryBudget = _config.ScanMemoryBudget,
            };

            await Task.Run(() =>
                _stateReader.RunTreeVisitor(visitor, header, options), linkedCts.Token).ConfigureAwait(false);

            StateCompositionStats stats = visitor.GetStats(header.Number, header.StateRoot);
            TrieDepthDistribution dist = visitor.GetTrieDistribution();

            _stateHolder.SetBaseline(stats, dist);
            _stateHolder.MarkScanCompleted(header.Number, header.StateRoot!, sw.Elapsed);

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: scan completed in {sw.Elapsed}. " +
                             $"Accounts={stats.AccountsTotal}, Contracts={stats.ContractsTotal}, " +
                             $"StorageSlots={stats.StorageSlotsTotal}");

            return stats;
        }
        finally
        {
            _currentScanCts = null;
            if (acquired)
                _scanLock.Release();
        }
    }

    public Task<TrieDepthDistribution> GetTrieDistributionAsync(BlockHeader header, CancellationToken ct)
    {
        if (_stateHolder.IsInitialized)
            return Task.FromResult(_stateHolder.CurrentDistribution);

        throw new InvalidOperationException(
            "No cached distribution available. Run statecomp_getStats() first to trigger a scan.");
    }

    public void CancelScan()
    {
        _currentScanCts?.Cancel();
    }
}
