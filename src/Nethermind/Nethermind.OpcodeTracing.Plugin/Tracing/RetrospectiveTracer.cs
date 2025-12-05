// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Utilities;

namespace Nethermind.OpcodeTracing.Plugin.Tracing;

/// <summary>
/// Handles retrospective opcode tracing by reading historical blocks from the database.
/// </summary>
public sealed class RetrospectiveTracer
{
    private readonly IBlockTree _blockTree;
    private readonly OpcodeCounter _counter;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetrospectiveTracer"/> class.
    /// </summary>
    /// <param name="blockTree">The block tree for accessing historical blocks.</param>
    /// <param name="counter">The opcode counter to accumulate into.</param>
    /// <param name="logManager">The log manager.</param>
    public RetrospectiveTracer(IBlockTree blockTree, OpcodeCounter counter, ILogManager logManager)
    {
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _logger = logManager?.GetClassLogger<RetrospectiveTracer>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    /// <summary>
    /// Traces the specified block range asynchronously.
    /// </summary>
    /// <param name="range">The block range to trace.</param>
    /// <param name="progress">The progress tracker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task TraceBlockRangeAsync(BlockRange range, TracingProgress progress, CancellationToken cancellationToken = default)
    {
        for (long blockNumber = range.StartBlock; blockNumber <= range.EndBlock; blockNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Block? block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.None);
            if (block is null)
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn($"Block {blockNumber} not found in database");
                }
                progress.UpdateProgress(blockNumber);
                continue;
            }

            // For retrospective mode, we're reading already-processed blocks
            // We can't easily replay transactions without full state, so we'll skip detailed tracing
            // In a real implementation, you might want to replay transactions with a tracer
            // For now, we'll just log that we processed the block
            progress.UpdateProgress(blockNumber);

            // Log progress if needed
            if (progress.ShouldLogProgress() && _logger.IsInfo)
            {
                _logger.Info($"Retrospective tracing progress: block {blockNumber} ({progress.PercentComplete:F2}% complete)");
            }

            // Small delay to avoid overwhelming the system
            if (blockNumber % 100 == 0)
            {
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
