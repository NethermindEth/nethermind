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
public sealed class StateCompositionService(
    IStateReader stateReader,
    StateCompositionStateHolder stateHolder,
    IStateCompositionConfig config,
    ILogManager logManager)
    : IStateCompositionService
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public async Task<StateCompositionStats> AnalyzeAsync(BlockHeader header, CancellationToken ct)
    {
        if (!await _scanLock.WaitAsync(TimeSpan.FromSeconds(config.ScanQueueTimeoutSeconds), ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                "Scan already in progress. Use statecomp_getCachedStats() for last results.");

        try
        {
            stateHolder.MarkScanStarted();
            Stopwatch sw = Stopwatch.StartNew();

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: starting full scan at block {header.Number}, root {header.StateRoot}");

            using StateCompositionVisitor visitor = new(logManager);

            VisitingOptions options = new()
            {
                MaxDegreeOfParallelism = config.ScanParallelism,
                FullScanMemoryBudget = config.ScanMemoryBudget,
            };

            await Task.Run(() =>
                stateReader.RunTreeVisitor(visitor, header, options), ct).ConfigureAwait(false);

            StateCompositionStats stats = visitor.GetStats(header.Number, header.StateRoot);
            TrieDepthDistribution dist = visitor.GetTrieDistribution();

            stateHolder.SetBaseline(stats, dist);
            stateHolder.MarkScanCompleted(header.Number, header.StateRoot!, sw.Elapsed);

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: scan completed in {sw.Elapsed}. " +
                             $"Accounts={stats.AccountsTotal}, Contracts={stats.ContractsTotal}, " +
                             $"StorageSlots={stats.StorageSlotsTotal}");

            return stats;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    public async Task<TrieDepthDistribution> GetTrieDistributionAsync(BlockHeader header, CancellationToken ct)
    {
        if (stateHolder.IsInitialized)
            return stateHolder.CurrentDistribution;

        // No cached data — run a full scan first
        await AnalyzeAsync(header, ct).ConfigureAwait(false);
        return stateHolder.CurrentDistribution;
    }
}
