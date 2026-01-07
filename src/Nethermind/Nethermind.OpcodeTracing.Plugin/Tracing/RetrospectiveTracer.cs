// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Utilities;

namespace Nethermind.OpcodeTracing.Plugin.Tracing;

/// <summary>
/// Handles retrospective opcode tracing by reading historical blocks from the database.
/// Supports parallel processing via MaxDegreeOfParallelism configuration.
/// </summary>
public sealed class RetrospectiveTracer
{
    private readonly IBlockTree _blockTree;
    private readonly OpcodeCounter _counter;
    private readonly ILogger _logger;
    private readonly int _maxDegreeOfParallelism;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetrospectiveTracer"/> class.
    /// </summary>
    /// <param name="blockTree">The block tree for accessing historical blocks.</param>
    /// <param name="counter">The opcode counter to accumulate into.</param>
    /// <param name="maxDegreeOfParallelism">Maximum degree of parallelism. 0 or negative uses processor count.</param>
    /// <param name="logManager">The log manager.</param>
    public RetrospectiveTracer(IBlockTree blockTree, OpcodeCounter counter, int maxDegreeOfParallelism, ILogManager logManager)
    {
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _maxDegreeOfParallelism = maxDegreeOfParallelism <= 0 ? Environment.ProcessorCount : maxDegreeOfParallelism;
        _logger = logManager?.GetClassLogger<RetrospectiveTracer>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    /// <summary>
    /// Traces the specified block range asynchronously with parallel processing.
    /// </summary>
    /// <param name="range">The block range to trace.</param>
    /// <param name="progress">The progress tracker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task TraceBlockRangeAsync(BlockRange range, TracingProgress progress, CancellationToken cancellationToken = default)
    {
        if (_logger.IsInfo)
        {
            _logger.Info($"Retrospective tracing with MaxDegreeOfParallelism={_maxDegreeOfParallelism}");
        }

        // Create enumerable of block numbers for parallel processing
        var blockNumbers = Enumerable.Range(0, (int)range.Count)
            .Select(i => range.StartBlock + i);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(blockNumbers, parallelOptions, async (blockNumber, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            Block? block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.None);
            if (block is null)
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn($"Block {blockNumber} not found in database");
                }
                progress.UpdateProgress(blockNumber);
                return;
            }

            // Process transactions in the block
            if (block.Transactions is not null && block.Transactions.Length > 0)
            {
                foreach (var transaction in block.Transactions)
                {
                    ct.ThrowIfCancellationRequested();

                    // Analyze transaction bytecode for opcodes
                    AnalyzeTransactionOpcodes(transaction);
                }
            }

            progress.UpdateProgress(blockNumber);

            // Log progress if needed (thread-safe check)
            if (progress.ShouldLogProgress() && _logger.IsInfo)
            {
                _logger.Info($"Retrospective tracing progress: block {blockNumber} ({progress.PercentComplete:F2}% complete)");
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Analyzes transaction bytecode to extract and count opcodes.
    /// This provides a static analysis of opcodes present in the transaction data.
    /// Note: This counts opcodes in the transaction input data (contract calls/deployments),
    /// not the actual executed opcodes which would require full transaction replay.
    /// </summary>
    /// <param name="transaction">The transaction to analyze.</param>
    private void AnalyzeTransactionOpcodes(Transaction transaction)
    {
        if (transaction.Data.Length == 0)
        {
            return;
        }

        // For contract creation transactions, analyze the init code
        // For contract calls, the data is typically ABI-encoded function calls
        // We'll scan the bytecode looking for valid EVM opcodes
        ReadOnlySpan<byte> data = transaction.Data.Span;

        for (int i = 0; i < data.Length; i++)
        {
            byte opcodeByte = data[i];

            // Check if this byte is a valid EVM opcode
            if (Enum.IsDefined(typeof(Nethermind.Evm.Instruction), opcodeByte))
            {
                // Count this opcode
                _counter.Increment(opcodeByte);

                // Handle PUSH instructions which have immediate data following them
                // PUSH1-PUSH32 are opcodes 0x60-0x7F
                if (opcodeByte >= 0x60 && opcodeByte <= 0x7F)
                {
                    int pushSize = opcodeByte - 0x5F; // PUSH1 = 0x60 pushes 1 byte
                    i += pushSize; // Skip the push data bytes
                }
            }
        }
    }
}
