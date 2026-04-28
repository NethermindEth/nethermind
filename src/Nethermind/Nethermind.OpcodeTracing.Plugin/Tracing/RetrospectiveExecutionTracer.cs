// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
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
    private readonly IEthereumEcdsa _ecdsa;
    private readonly OpcodeCounter _counter;
    private readonly ILogger _logger;
    private readonly int _maxDegreeOfParallelism;
    private readonly ConcurrentQueue<long> _skippedBlocks = new();

    /// <summary>
    /// Gets the block numbers that were skipped due to unavailable state.
    /// </summary>
    public IReadOnlyCollection<long> SkippedBlocks => _skippedBlocks;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetrospectiveExecutionTracer"/> class.
    /// </summary>
    /// <param name="blockTree">The block tree for finding blocks.</param>
    /// <param name="specProvider">The spec provider for getting release specs.</param>
    /// <param name="txProcessingEnvFactory">Factory to create isolated transaction processing environments per block.</param>
    /// <param name="ecdsa">ECDSA helper used to recover sender addresses on transaction clones before tracing.</param>
    /// <param name="counter">The opcode counter to accumulate into.</param>
    /// <param name="maxDegreeOfParallelism">Maximum degree of parallelism. 0 or negative uses processor count.</param>
    /// <param name="logManager">The log manager.</param>
    /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
    public RetrospectiveExecutionTracer(
        IBlockTree blockTree,
        ISpecProvider specProvider,
        IReadOnlyTxProcessingEnvFactory txProcessingEnvFactory,
        IEthereumEcdsa ecdsa,
        OpcodeCounter counter,
        int maxDegreeOfParallelism,
        ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(blockTree);
        ArgumentNullException.ThrowIfNull(specProvider);
        ArgumentNullException.ThrowIfNull(txProcessingEnvFactory);
        ArgumentNullException.ThrowIfNull(ecdsa);
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(logManager);

        _blockTree = blockTree;
        _specProvider = specProvider;
        _txProcessingEnvFactory = txProcessingEnvFactory;
        _ecdsa = ecdsa;
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

        // Generate block numbers without int cast to avoid overflow for large ranges
        IEnumerable<long> blockNumbers = GenerateBlockNumbers(range);

        // Configure parallel processing options
        ParallelOptions parallelOptions = new()
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

    private static IEnumerable<long> GenerateBlockNumbers(BlockRange range)
    {
        for (long i = range.StartBlock; i <= range.EndBlock; i++)
        {
            yield return i;
        }
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
            _skippedBlocks.Enqueue(blockNumber);
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
            _skippedBlocks.Enqueue(blockNumber);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsWarn)
            {
                _logger.Warn($"Failed to process block {blockNumber}: {ex.Message}");
            }
            _skippedBlocks.Enqueue(blockNumber);
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

            // Clone the header to avoid mutating the shared cached instance from BlockTree
            BlockHeader tracingHeader = block.Header.Clone();

            // Get spec and calculate blob base fee for this block
            IReleaseSpec spec = _specProvider.GetSpec(tracingHeader);
            UInt256 blobBaseFee = UInt256.Zero;
            if (spec.IsEip4844Enabled && tracingHeader.ExcessBlobGas.HasValue)
            {
                BlobGasCalculator.TryCalculateFeePerBlobGas(tracingHeader, spec.BlobBaseFeeUpdateFraction, out blobBaseFee);
            }

            // Sender recovery has to happen BEFORE TransactionProcessor.Trace(): the processor
            // dereferences tx.SenderAddress when building the TxExecutionContext (via Address.ToHash())
            // *before* its own RecoverSenderIfNeeded runs, so a DB-decoded tx with a null sender
            // would NRE. The normal block-processing pipeline solves this by running the
            // RecoverSignatures preprocessor step ahead of TransactionProcessor — we bypass that
            // pipeline here (we call Trace directly on the read-only scope), so we must mirror it.
            //
            // We recover onto CLONES rather than the BlockTree-cached instances. FindBlock can return
            // Block/Transaction objects that are shared across the client (RPC responses, mempool
            // lookups, subsequent FindBlock hits); mutating tx.SenderAddress in-place would race
            // with those readers once MaxDegreeOfParallelism > 1 and leak our recovery into paths
            // that expect the cache to be immutable. Transaction.CopyTo gives us independent
            // per-tracer instances we can freely mutate.
            Transaction[] tracedTxs = new Transaction[block.Transactions.Length];
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                Transaction clone = new();
                block.Transactions[i].CopyTo(clone);
                clone.SenderAddress ??= _ecdsa.RecoverAddress(clone, !spec.ValidateChainId);
                tracedTxs[i] = clone;
            }

            // Reset GasUsed to 0 for tracing on the cloned header - the finalized header has the total gas used,
            // but ValidateGas() checks against remaining gas (GasLimit - GasUsed).
            // Without this reset, all transactions fail the gas limit check.
            tracingHeader.GasUsed = 0;

            if (_logger.IsDebug)
            {
                _logger.Debug($"Processing block {block.Number} with {block.Transactions.Length} txs (original GasUsed: {block.Header.GasUsed}, GasLimit: {tracingHeader.GasLimit})");
            }

            long blockTotalOpcodes = 0;
            int successfulTxs = 0;
            int failedTxs = 0;

            // Create block execution context using the cloned header
            BlockExecutionContext blockExecutionContext = new(tracingHeader, spec, blobBaseFee);

            // Process each transaction in the block (using the recovered clones above)
            foreach (Transaction tx in tracedTxs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Create opcode counting tracer for this transaction
                OpcodeCountingTxTracer txTracer = new();

                // Execute transaction with tracing
                TransactionResult result = scope.TransactionProcessor.Trace(tx, in blockExecutionContext, txTracer);

                if (result.TransactionExecuted)
                {
                    successfulTxs++;
                    // Accumulate opcode counts from this transaction
                    txTracer.AccumulateInto(blockOpcodes);
                    blockTotalOpcodes += txTracer.TotalOpcodes;
                }
                else
                {
                    failedTxs++;
                    if (_logger.IsDebug)
                    {
                        _logger.Debug($"Block {block.Number} tx {tx.Hash} failed: {result.ErrorDescription}");
                    }
                }
            }

            if (_logger.IsDebug)
            {
                _logger.Debug($"Block {block.Number}: {successfulTxs} txs executed, {failedTxs} failed, {blockTotalOpcodes} total opcodes");
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
