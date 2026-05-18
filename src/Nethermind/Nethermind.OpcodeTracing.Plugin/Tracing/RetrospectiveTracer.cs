// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FastEnumUtility;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Utilities;

namespace Nethermind.OpcodeTracing.Plugin.Tracing;

/// <summary>
/// Handles retrospective opcode tracing by reading historical blocks from the database.
/// Supports parallel processing via MaxDegreeOfParallelism configuration.
/// </summary>
public sealed class RetrospectiveTracer(IBlockTree blockTree, OpcodeCounter counter, int maxDegreeOfParallelism, ILogManager logManager)
{
    /// <summary>
    /// Precomputed lookup table for valid EVM opcodes. Avoids Enum.IsDefined overhead in tight loops.
    /// </summary>
    private static readonly bool[] s_validOpcodes = BuildValidOpcodesTable();

    private static bool[] BuildValidOpcodesTable()
    {
        bool[] table = new bool[256];
        foreach (Instruction instruction in FastEnum.GetValues<Instruction>())
        {
            table[(byte)instruction] = true;
        }
        return table;
    }

    private readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
    private readonly OpcodeCounter _counter = counter ?? throw new ArgumentNullException(nameof(counter));
    private readonly ILogger _logger = logManager?.GetClassLogger<RetrospectiveTracer>() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly int _maxDegreeOfParallelism = maxDegreeOfParallelism <= 0 ? Environment.ProcessorCount : maxDegreeOfParallelism;

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

        // Generate block numbers without int cast to avoid overflow for large ranges
        IEnumerable<long> blockNumbers = GenerateBlockNumbers(range);

        ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = _maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(blockNumbers, parallelOptions, (blockNumber, ct) =>
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
                return ValueTask.CompletedTask;
            }

            // Process transactions in the block
            if (block.Transactions is not null && block.Transactions.Length > 0)
            {
                foreach (Transaction transaction in block.Transactions)
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

            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
    }

    private static IEnumerable<long> GenerateBlockNumbers(BlockRange range)
    {
        for (long i = range.StartBlock; i <= range.EndBlock; i++)
        {
            yield return i;
        }
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

            // Check if this byte is a valid EVM opcode using precomputed lookup table
            if (s_validOpcodes[opcodeByte])
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
