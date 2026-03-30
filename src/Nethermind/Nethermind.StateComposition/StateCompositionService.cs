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
    private readonly StateCompositionStateHolder _stateHolder;
    private readonly IStateCompositionConfig _config;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public StateCompositionService(
        IStateReader stateReader,
        StateCompositionStateHolder stateHolder,
        IStateCompositionConfig config,
        ILogManager logManager)
    {
        _stateReader = stateReader;
        _stateHolder = stateHolder;
        _config = config;
        _logManager = logManager;
        _logger = logManager.GetClassLogger();
    }

    public async Task<StateCompositionStats> AnalyzeAsync(BlockHeader header, CancellationToken ct)
    {
        if (!await _scanLock.WaitAsync(TimeSpan.FromSeconds(_config.ScanQueueTimeoutSeconds), ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                "Scan already in progress. Use statecomp_getScanProgress() to monitor or statecomp_getCachedStats() for last results.");

        try
        {
            _stateHolder.MarkScanStarted();
            Stopwatch sw = Stopwatch.StartNew();

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: starting full scan at block {header.Number}, root {header.StateRoot}");

            using StateCompositionVisitor visitor = new(_logManager);
            visitor.OnProgress += accounts =>
            {
                // Rough progress estimate: mainnet has ~300M accounts
                _stateHolder.UpdateProgress(accounts / 300_000_000.0);
            };

            VisitingOptions options = new()
            {
                MaxDegreeOfParallelism = _config.ScanParallelism,
                FullScanMemoryBudget = _config.ScanMemoryBudget,
            };

            await Task.Run(() =>
                _stateReader.RunTreeVisitor(visitor, header, options), ct).ConfigureAwait(false);

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsError)
                _logger.Error("StateComposition: scan failed", ex);
            throw;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    public async Task<TrieDepthDistribution> GetTrieDistributionAsync(BlockHeader header, CancellationToken ct)
    {
        if (_stateHolder.IsInitialized)
            return _stateHolder.CurrentDistribution;

        // No cached data — run a full scan first
        await AnalyzeAsync(header, ct).ConfigureAwait(false);
        return _stateHolder.CurrentDistribution;
    }
}
