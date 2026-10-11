// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;

namespace Nethermind.BeaconChain.Test.Sync;

internal static class ColumnFetchTestExtensions
{
    public static async Task SettleColumnFetchesAsync(this BeaconSyncOrchestrator orchestrator, CancellationToken token)
    {
        await orchestrator.ProcessQueuedAsync(token);
        while (orchestrator.ColumnFetchesInFlight > 0)
        {
            await orchestrator.WaitForWorkAsync(token);
            await orchestrator.ProcessQueuedAsync(token);
        }
    }

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

    /// <summary>Bound off-worker fetch waits so a missing completion fails rather than hangs the test.</summary>
    public static readonly TimeSpan FetchWaitTimeout = TimeSpan.FromSeconds(10);

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

    public static async Task<BlockImportResult> ImportAndSettleAsync(this BeaconSyncOrchestrator orchestrator, IBlockImporter importer, ForkedSignedBeaconBlock block, CancellationToken token)
    {
        BlockImportResult result = await orchestrator.ImportBlockAsync(block, token);
        await orchestrator.SettleColumnFetchesAsync(token);
        return importer.IsKnown(block.ComputeMessageRoot()) ? BlockImportResult.Imported : result;
    }
}
