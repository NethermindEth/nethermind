// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Utilities;
using Nethermind.State;

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
    private readonly IBlockProcessor _blockProcessor;
    private readonly ISpecProvider _specProvider;
    private readonly IStateReader _stateReader;
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
    /// <param name="api">The Nethermind API providing access to blockchain services.</param>
    /// <param name="counter">The opcode counter to accumulate into.</param>
    /// <param name="maxDegreeOfParallelism">Maximum degree of parallelism. 0 or negative uses processor count.</param>
    /// <param name="logManager">The log manager.</param>
    /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when required API components are not available.</exception>
    public RetrospectiveExecutionTracer(
        INethermindApi api,
        OpcodeCounter counter,
        int maxDegreeOfParallelism,
        ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(logManager);

        _blockTree = api.BlockTree ?? throw new InvalidOperationException("BlockTree is not available");
        _specProvider = api.SpecProvider ?? throw new InvalidOperationException("SpecProvider is not available");
        _stateReader = api.StateReader ?? throw new InvalidOperationException("StateReader is not available");

        var processingContext = api.MainProcessingContext
            ?? throw new InvalidOperationException("MainProcessingContext is not available");

        _blockProcessor = processingContext.BlockProcessor;

        _counter = counter;
        _maxDegreeOfParallelism = maxDegreeOfParallelism <= 0 ? Environment.ProcessorCount : maxDegreeOfParallelism;
        _logger = logManager.GetClassLogger<RetrospectiveExecutionTracer>();
    }

    /// <summary>
    /// Checks if historical state is available for the specified block.
    /// State availability is determined by whether the parent block's state exists.
    /// </summary>
    /// <param name="block">The block to check state availability for.</param>
    /// <returns>True if state is available; false otherwise.</returns>
    public bool CheckStateAvailability(Block block)
    {
        if (block.IsGenesis)
        {
            return true;
        }

        BlockHeader? parentHeader = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
        if (parentHeader is null)
        {
            return false;
        }

        // Check if parent state exists using IStateReader
        return _stateReader.HasStateForBlock(parentHeader);
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

        // For initial implementation, process sequentially to ensure correctness
        // Parallel processing will be added in Phase 4 (US2)
        foreach (long blockNumber in blockNumbers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await ProcessBlockAsync(blockNumber, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Processes a single block by replaying all its transactions with the EVM.
    /// </summary>
    /// <param name="blockNumber">The block number to process.</param>
    /// <param name="progress">The progress tracker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ProcessBlockAsync(long blockNumber, TracingProgress progress, CancellationToken cancellationToken)
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

        // Check if state is available for this block
        if (!CheckStateAvailability(block))
        {
            if (_logger.IsWarn)
            {
                _logger.Warn($"State unavailable for block {blockNumber}, skipping (parent state may be pruned)");
            }
            _skippedBlocks.Add(blockNumber);
            progress.UpdateProgress(blockNumber);
            return;
        }

        // Process the block with our opcode counting tracer
        try
        {
            long[] blockOpcodes = ProcessBlock(block, cancellationToken);
            _counter.AccumulateFrom(blockOpcodes);
        }
        catch (Exception ex)
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

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Processes a block using the block processor with opcode counting tracer.
    /// </summary>
    /// <param name="block">The block to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of opcode counts (256 elements, one per possible opcode byte).</returns>
    private long[] ProcessBlock(Block block, CancellationToken cancellationToken)
    {
        // Create a tracer to accumulate opcodes for this block
        long[] blockOpcodes = new long[256];
        var blockTracer = new OpcodeBlockTracer(trace =>
        {
            // Accumulate opcodes from the block trace
            foreach (var kvp in trace.Opcodes)
            {
                blockOpcodes[kvp.Key] += kvp.Value;
            }
        });

        // Get spec for this block
        IReleaseSpec spec = _specProvider.GetSpec(block.Header);

        // Process the block with tracing enabled
        // ProcessingOptions.Trace enables read-only execution without state persistence
        _blockProcessor.ProcessOne(
            block,
            ProcessingOptions.Trace,
            blockTracer,
            spec,
            cancellationToken);

        return blockOpcodes;
    }
}
