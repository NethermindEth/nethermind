// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Utilities;
using Nethermind.Trie;

namespace Nethermind.OpcodeTracing.Plugin.Tracing;

/// <summary>
/// Handles retrospective opcode tracing by replaying historical transactions with actual EVM execution.
/// Unlike <see cref="RetrospectiveTracer"/> which only analyzes bytecode statically, this tracer
/// executes transactions to capture all opcodes including those from internal calls.
/// Supports parallel block processing via MaxDegreeOfParallelism configuration.
/// </summary>
public sealed class RetrospectiveExecutionTracer
{
    private readonly IBlockTree _blockTree;
    private readonly IReadOnlyTxProcessingEnvFactory _txProcessingEnvFactory;
    private readonly ISpecProvider _specProvider;
    private readonly OpcodeCounter _counter;
    private readonly ILogger _logger;
    private readonly int _maxDegreeOfParallelism;
    private readonly ConcurrentBag<long> _skippedBlocks = new();

    /// <summary>
    /// Gets the block numbers that were skipped due to unavailable state.
    /// </summary>
    public IReadOnlyCollection<long> SkippedBlocks => _skippedBlocks.ToArray();

    /// <summary>
    /// Initializes a new instance of the <see cref="RetrospectiveExecutionTracer"/> class.
    /// </summary>
    /// <param name="blockTree">The block tree for finding blocks.</param>
    /// <param name="specProvider">The spec provider for getting release specs.</param>
    /// <param name="txProcessingEnvFactory">Factory to create isolated transaction processing environments per block.</param>
    /// <param name="counter">The opcode counter to accumulate into.</param>
    /// <param name="maxDegreeOfParallelism">Maximum degree of parallelism. 0 or negative uses processor count.</param>
    /// <param name="logManager">The log manager.</param>
    /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
    public RetrospectiveExecutionTracer(
        IBlockTree blockTree,
        ISpecProvider specProvider,
        IReadOnlyTxProcessingEnvFactory txProcessingEnvFactory,
        OpcodeCounter counter,
        int maxDegreeOfParallelism,
        ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(blockTree);
        ArgumentNullException.ThrowIfNull(specProvider);
        ArgumentNullException.ThrowIfNull(txProcessingEnvFactory);
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(logManager);

        _blockTree = blockTree;
        _specProvider = specProvider;
        _txProcessingEnvFactory = txProcessingEnvFactory;
        _counter = counter;
        _maxDegreeOfParallelism = maxDegreeOfParallelism <= 0 ? Environment.ProcessorCount : maxDegreeOfParallelism;
        _logger = logManager.GetClassLogger<RetrospectiveExecutionTracer>();
    }

    /// <summary>
    /// Traces the specified block range asynchronously with parallel processing.
    /// Blocks with unavailable state are skipped with warnings logged.
    /// </summary>
    /// <param name="range">The block range to trace.</param>
    /// <param name="progress">The progress tracker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task TraceBlockRangeAsync(BlockRange range, TracingProgress progress, CancellationToken cancellationToken = default)
    {
        if (_logger.IsInfo)
        {
            _logger.Info($"RetrospectiveExecution tracing with MaxDegreeOfParallelism={_maxDegreeOfParallelism}");
        }

        // Create enumerable of block numbers for processing
        var blockNumbers = Enumerable.Range(0, (int)range.Count)
            .Select(i => range.StartBlock + i);

        // Configure parallel processing options
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        // Process blocks in parallel with isolated state per block
        await Parallel.ForEachAsync(blockNumbers, parallelOptions, (blockNumber, ct) =>
        {
            ProcessBlockSync(blockNumber, progress, ct);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Processes a single block by replaying all its transactions with the EVM.
    /// This method is synchronous and thread-safe for parallel execution.
    /// State availability is checked via exception handling rather than pre-checking,
    /// matching the pattern used by debug_traceBlock.
    /// </summary>
    /// <param name="blockNumber">The block number to process.</param>
    /// <param name="progress">The progress tracker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private void ProcessBlockSync(long blockNumber, TracingProgress progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Block? block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.None);
        if (block is null)
        {
            if (_logger.IsWarn)
            {
                _logger.Warn($"Block {blockNumber} not found in database");
            }
            _skippedBlocks.Add(blockNumber);
            progress.UpdateProgress(blockNumber);
            return;
        }

        // Process the block with our opcode counting tracer
        // State availability is determined by catching MissingTrieNodeException during processing
        try
        {
            long[] blockOpcodes = ProcessBlock(block, cancellationToken);
            _counter.AccumulateFrom(blockOpcodes);
        }
        catch (MissingTrieNodeException ex)
        {
            // State is genuinely unavailable (pruned or not synced)
            if (_logger.IsWarn)
            {
                _logger.Warn($"State unavailable for block {blockNumber}: {ex.Message}");
            }
            _skippedBlocks.Add(blockNumber);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsWarn)
            {
                _logger.Warn($"Failed to process block {blockNumber}: {ex.Message}");
            }
            _skippedBlocks.Add(blockNumber);
        }

        progress.UpdateProgress(blockNumber);

        // Log progress if needed
        if (progress.ShouldLogProgress() && _logger.IsInfo)
        {
            _logger.Info($"RetrospectiveExecution tracing progress: block {blockNumber} ({progress.PercentComplete:F2}% complete)");
        }
    }

    /// <summary>
    /// Processes a block by executing each transaction with an isolated read-only transaction processor.
    /// Creates an independent processing environment for each block to ensure thread-safety during parallel processing.
    /// </summary>
    /// <param name="block">The block to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of opcode counts (256 elements, one per possible opcode byte).</returns>
    private long[] ProcessBlock(Block block, CancellationToken cancellationToken)
    {
        long[] blockOpcodes = new long[256];

        // Get parent header for state context
        BlockHeader? parentHeader = block.IsGenesis
            ? null
            : _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);

        // Create independent processing environment for this block (thread-safe for parallel processing)
        IReadOnlyTxProcessorSource txProcessorSource = _txProcessingEnvFactory.Create();
        try
        {
            // Create isolated processing scope based on parent state
            using IReadOnlyTxProcessingScope scope = txProcessorSource.Build(parentHeader);

            // Get spec and calculate blob base fee for this block
            IReleaseSpec spec = _specProvider.GetSpec(block.Header);
            UInt256 blobBaseFee = UInt256.Zero;
            if (spec.IsEip4844Enabled && block.Header.ExcessBlobGas.HasValue)
            {
                BlobGasCalculator.TryCalculateFeePerBlobGas(block.Header, spec.BlobBaseFeeUpdateFraction, out blobBaseFee);
            }

            // Create block execution context
            BlockExecutionContext blockExecutionContext = new(block.Header, spec, blobBaseFee);

            // Process each transaction in the block
            foreach (Transaction tx in block.Transactions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Create opcode counting tracer for this transaction
                OpcodeCountingTxTracer txTracer = new();

                // Execute transaction with tracing (no validation, commits state within scope)
                scope.TransactionProcessor.Trace(tx, in blockExecutionContext, txTracer);

                // Accumulate opcode counts from this transaction
                txTracer.AccumulateInto(blockOpcodes);
            }
        }
        finally
        {
            // Dispose the processing environment if it's disposable
            (txProcessorSource as IDisposable)?.Dispose();
        }

        return blockOpcodes;
    }
}
