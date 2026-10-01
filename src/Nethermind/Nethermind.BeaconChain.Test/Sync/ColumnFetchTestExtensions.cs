// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>Drives the orchestrator's queue by hand while a by-root column fetch runs off the worker, without a sleep or a wall-clock wait.</summary>
internal static class ColumnFetchTestExtensions
{
    /// <summary>Processes the queued work, then waits for each running column fetch to end and processes what it queued, in the order the worker would.</summary>
    public static async Task SettleColumnFetchesAsync(this BeaconSyncOrchestrator orchestrator, CancellationToken token)
    {
        await orchestrator.ProcessQueuedAsync(token);
        while (orchestrator.ColumnFetchesInFlight > 0)
        {
            await orchestrator.WaitForWorkAsync(token);
            await orchestrator.ProcessQueuedAsync(token);
        }
    }

    /// <summary>Like <see cref="SettleColumnFetchesAsync"/> but counts the worker passes and fails at <paramref name="maxPasses"/> instead of looping on work that feeds itself.</summary>
    public static async Task<int> SettleWithinAsync(this BeaconSyncOrchestrator orchestrator, int maxPasses, CancellationToken token)
    {
        int passes = 0;
        while (orchestrator.QueuedWorkCount > 0 || orchestrator.ColumnFetchesInFlight > 0 || orchestrator.AncestorFetchesInFlight > 0)
        {
            if (++passes > maxPasses)
            {
                throw new InvalidOperationException($"The worker still had work after {maxPasses} passes");
            }

            if (orchestrator.QueuedWorkCount == 0)
            {
                await orchestrator.WaitForWorkAsync(token);
            }

            await orchestrator.ProcessQueuedAsync(token);
        }

        return passes;
    }

    /// <summary>The longest a test waits for a fetch running off the worker to queue its result, so a fetch that never ends fails the test instead of hanging it.</summary>
    public static readonly TimeSpan FetchWaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Processes the gossip <paramref name="block"/>, then the work its ancestor fetches queue until none runs, as the worker would;
    /// fails after <paramref name="maxPasses"/> passes or <see cref="FetchWaitTimeout"/> without queued work.
    /// </summary>
    public static async Task ProcessGossipBlockAndFetchAncestorsAsync(this BeaconSyncOrchestrator orchestrator, ForkedSignedBeaconBlock block, CancellationToken token, int maxPasses = 64)
    {
        await orchestrator.ProcessGossipBlockAsync(block, token);
        int passes = 0;
        while (orchestrator.AncestorFetchesInFlight > 0)
        {
            if (++passes > maxPasses)
            {
                throw new InvalidOperationException($"{orchestrator.AncestorFetchesInFlight} ancestor fetches still ran after {maxPasses} passes");
            }

            if (orchestrator.QueuedWorkCount == 0)
            {
                using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(FetchWaitTimeout);
                await orchestrator.WaitForWorkAsync(wait.Token);
            }

            await orchestrator.ProcessQueuedAsync(token);
        }
    }

    /// <summary>Imports <paramref name="block"/>, lets the fetch its deferral starts and the retry the fetched columns wake run, and reports <see cref="BlockImportResult.Imported"/> once <paramref name="importer"/> knows the block.</summary>
    public static async Task<BlockImportResult> ImportAndSettleAsync(this BeaconSyncOrchestrator orchestrator, IBlockImporter importer, ForkedSignedBeaconBlock block, CancellationToken token)
    {
        BlockImportResult result = await orchestrator.ImportBlockAsync(block, token);
        await orchestrator.SettleColumnFetchesAsync(token);
        return importer.IsKnown(block.ComputeMessageRoot()) ? BlockImportResult.Imported : result;
    }
}
