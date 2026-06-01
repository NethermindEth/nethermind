// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Init.Steps;

/// <summary>
/// Re-processes the most recent <see cref="IBlocksConfig.PreWarmReprocessBlockCount"/> canonical blocks at
/// startup, read-only, to warm the cross-block state caches before normal operation begins.
/// </summary>
/// <remarks>
/// Runs after <see cref="InitializeBlockchain"/> (state and processor are ready) and before
/// <see cref="StartBlockProcessor"/> (the live processing loop). Each block is replayed via
/// <see cref="IBlockchainProcessor.Process"/> with <see cref="ProcessingOptions.Trace"/>, which executes the
/// block against its parent state without persisting, validating, or moving the chain head — the same
/// read-only replay path the tracing RPCs use. The side effect we want is purely cache population: the
/// active state backend's cross-block cache (sparse trie preserved arena, flat trie-node cache) and the
/// RocksDB/OS page cache end up warm, so the first real payloads after startup hit the steady-state
/// (warm) cost instead of the cold-warmup cost. Backend-agnostic: it works identically for the sparse
/// trie, the flat trie-node cache, and the legacy Patricia path, which makes it a fair pre-warm for
/// before/after and master-vs-branch benchmark comparisons.
/// No-op when the count is 0 (default) so production startup is unaffected.
/// </remarks>
[RunnerStepDependencies(typeof(InitializeBlockchain))]
public class WarmUpReprocessBlocks(
    IMainProcessingContext mainProcessingContext,
    IBlockTree blockTree,
    IBlocksConfig blocksConfig,
    ILogManager logManager) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<WarmUpReprocessBlocks>();

    public Task Execute(CancellationToken cancellationToken)
    {
        int count = blocksConfig.PreWarmReprocessBlockCount;
        if (count <= 0) return Task.CompletedTask;

        BlockHeader? head = blockTree.Head?.Header;
        if (head is null)
        {
            if (_logger.IsInfo) _logger.Info("PreWarmReprocessBlockCount set but no head block available; skipping cache warm-up.");
            return Task.CompletedTask;
        }

        // Stay strictly above genesis: replaying genesis has no parent state to run on top of, and the
        // warm-up's value is in the recent working set anyway.
        long fromNumber = System.Math.Max(1, head.Number - count + 1);
        long toNumber = head.Number;
        long planned = toNumber - fromNumber + 1;
        if (planned <= 0) return Task.CompletedTask;

        if (_logger.IsInfo) _logger.Info($"Warming cross-block caches by re-processing blocks {fromNumber}..{toNumber} ({planned} blocks, read-only)...");

        Stopwatch sw = Stopwatch.StartNew();
        int processed = 0;
        for (long number = fromNumber; number <= toNumber; number++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            Block? block = blockTree.FindBlock(number, BlockTreeLookupOptions.RequireCanonical);
            if (block is null) continue;

            try
            {
                // Trace = ForceProcessing | ReadOnlyChain | LoadNonceFromState | NoValidation:
                // re-execute on top of the parent state, discard all changes, do not move the head.
                mainProcessingContext.BlockchainProcessor.Process(
                    block, ProcessingOptions.Trace, NullBlockTracer.Instance, cancellationToken);
                processed++;
            }
            catch (System.Exception ex)
            {
                // Warm-up is best-effort; a single failed replay must not block startup.
                if (_logger.IsWarn) _logger.Warn($"Cache warm-up failed to re-process block {number}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        sw.Stop();
        if (_logger.IsInfo) _logger.Info($"Cross-block cache warm-up complete: re-processed {processed}/{planned} blocks in {sw.ElapsedMilliseconds} ms.");
        return Task.CompletedTask;
    }
}
