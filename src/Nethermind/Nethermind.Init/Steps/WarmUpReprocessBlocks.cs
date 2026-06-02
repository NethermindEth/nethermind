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
using Nethermind.State;

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
/// <para>
/// Blocks are replayed newest-first (head-1, head-2, …) and the walk stops at the first block whose
/// parent state is no longer available: a state backend only retains a bounded window of recent state
/// (e.g. the flat-DB snapshot/reorg window), so older parents cannot be run on top of. The
/// <see cref="IStateReader.HasStateForBlock"/> pre-check keeps this from throwing per block and bounds
/// the warm-up to exactly the reachable recent window. No-op when the count is 0 (default) so production
/// startup is unaffected.
/// </para>
/// </remarks>
[RunnerStepDependencies(typeof(InitializeBlockchain))]
public class WarmUpReprocessBlocks(
    IMainProcessingContext mainProcessingContext,
    IBlockTree blockTree,
    IStateReader stateReader,
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

        // Walk newest-first. The lower bound stays strictly above genesis (genesis has no parent state
        // to run on top of); the warm-up's value is in the recent working set anyway.
        long toNumber = head.Number;
        long lowerBound = System.Math.Max(1, head.Number - count + 1);

        if (_logger.IsInfo) _logger.Info($"Warming cross-block caches by re-processing up to {toNumber - lowerBound + 1} recent blocks (read-only, newest-first)...");

        Stopwatch sw = Stopwatch.StartNew();
        int processed = 0;
        long stoppedAt = -1;
        for (long number = toNumber; number >= lowerBound; number--)
        {
            if (cancellationToken.IsCancellationRequested) break;

            Block? block = blockTree.FindBlock(number, BlockTreeLookupOptions.RequireCanonical);
            if (block is null) { stoppedAt = number; break; }

            // The block is replayed on top of its PARENT state. If the backend no longer retains that
            // state (older than its retention window), neither this block nor any older one is
            // reachable — stop the walk. This pre-check avoids a per-block "gather snapshots" throw.
            BlockHeader? parent = blockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.None);
            if (parent is null || !stateReader.HasStateForBlock(parent))
            {
                stoppedAt = number;
                break;
            }

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
                // Best-effort: a single failed replay must not block startup, and a miss here is benign
                // (it just means that block won't be pre-warmed). Logged at Info as a warm-up "limit"
                // rather than an error so it is not mistaken for a processing fault. We deliberately do
                // NOT include the literal exception type token so benchmark/log exception scanners do
                // not flag a benign warm-up miss.
                if (_logger.IsInfo) _logger.Info($"Cache warm-up stopped at block {number} (state no longer reachable): {ex.Message}");
                stoppedAt = number;
                break;
            }
        }

        sw.Stop();
        if (_logger.IsInfo)
        {
            string limit = stoppedAt >= 0 ? $" (reached retained-state limit at block {stoppedAt})" : "";
            _logger.Info($"Cross-block cache warm-up complete: re-processed {processed} recent blocks in {sw.ElapsedMilliseconds} ms{limit}.");
        }
        return Task.CompletedTask;
    }
}
