// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Output;
using Nethermind.OpcodeTracing.Plugin.Utilities;

namespace Nethermind.OpcodeTracing.Plugin.Tracing;

/// <summary>
/// Handles real-time opcode tracing by attaching to live block processing.
/// Produces dual JSON output: cumulative totals + per-block files.
/// </summary>
public sealed class RealTimeTracer : IAsyncDisposable
{
    private readonly OpcodeCounter _counter;
    private readonly BlockRange _range;
    private readonly ILogger _logger;
    private readonly Action<long> _onBlockCompleted;
    private readonly string _outputDirectory;
    private readonly string _sessionId;

    // Dual output writers
    private readonly PerBlockTraceWriter _perBlockWriter;
    private readonly CumulativeTraceWriter _cumulativeWriter;
    private readonly AsyncFileWriteQueue _writeQueue;

    // Tracking state
    private readonly Stopwatch _stopwatch;
    private readonly DateTime _startedAt;
    private long _firstBlock = -1;
    private long _lastBlock = -1;
    private long _totalBlocksProcessed;
    private bool _rangeCompleted;

    /// <summary>
    /// Gets a value indicating whether the configured block range has been completed.
    /// </summary>
    public bool RangeCompleted => _rangeCompleted;

    /// <summary>
    /// Gets the block range being traced.
    /// </summary>
    public BlockRange Range => _range;

    /// <summary>
    /// Gets the session ID for this tracing session.
    /// </summary>
    public string SessionId => _sessionId;

    /// <summary>
    /// Initializes a new instance of the <see cref="RealTimeTracer"/> class.
    /// </summary>
    /// <param name="counter">The opcode counter to accumulate into.</param>
    /// <param name="range">The block range to trace.</param>
    /// <param name="outputDirectory">The output directory for JSON files.</param>
    /// <param name="sessionId">The unique session identifier for cumulative file naming.</param>
    /// <param name="onBlockCompleted">Callback invoked when a block in range is completed.</param>
    /// <param name="logManager">The log manager.</param>
    public RealTimeTracer(
        OpcodeCounter counter,
        BlockRange range,
        string outputDirectory,
        string sessionId,
        Action<long> onBlockCompleted,
        ILogManager logManager)
    {
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _range = range;
        _outputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _onBlockCompleted = onBlockCompleted ?? throw new ArgumentNullException(nameof(onBlockCompleted));
        _logger = logManager?.GetClassLogger<RealTimeTracer>() ?? throw new ArgumentNullException(nameof(logManager));

        // Initialize writers
        _perBlockWriter = new PerBlockTraceWriter(logManager);
        _cumulativeWriter = new CumulativeTraceWriter(outputDirectory, sessionId, logManager);
        _writeQueue = new AsyncFileWriteQueue(outputDirectory, _perBlockWriter, logManager);

        // Initialize timing
        _stopwatch = Stopwatch.StartNew();
        _startedAt = DateTime.UtcNow;

        if (_logger.IsInfo)
        {
            _logger.Info($"RealTime tracer initialized: session={sessionId}, range={range.StartBlock}-{range.EndBlock}");
        }
    }

    /// <summary>
    /// Handles a completed block trace.
    /// Writes per-block JSON file asynchronously and updates cumulative totals.
    /// Continues tracing indefinitely for all blocks >= StartBlock.
    /// </summary>
    /// <param name="trace">The block trace data.</param>
    internal void OnBlockCompleted(OpcodeBlockTrace trace)
    {
        if (trace is null)
        {
            return;
        }

        // Only skip blocks before the start block - trace all blocks at or after start
        if (trace.BlockNumber < _range.StartBlock)
        {
            return;
        }

        // Accumulate opcodes into global counter
        long[] blockCounts = new long[256];
        foreach (var (opcodeName, count) in trace.Opcodes)
        {
            byte opcodeValue = GetOpcodeByteFromName(opcodeName);
            blockCounts[opcodeValue] = count;
        }

        _counter.AccumulateFrom(blockCounts);

        // Update tracking state
        _totalBlocksProcessed++;
        if (_firstBlock < 0)
        {
            _firstBlock = trace.BlockNumber;
        }
        _lastBlock = trace.BlockNumber;

        // Create and enqueue per-block output
        PerBlockTraceOutput perBlockOutput = CreatePerBlockOutput(trace);
        _writeQueue.Enqueue(perBlockOutput);

        if (_logger.IsDebug)
        {
            _logger.Debug($"RealTime: block {trace.BlockNumber} processed, {trace.Opcodes.Count} unique opcodes");
        }

        // Update cumulative file
        _ = UpdateCumulativeFileAsync();

        // Notify callback
        _onBlockCompleted(trace.BlockNumber);

        // Check for initial range completion (log once, but continue tracing)
        if (!_rangeCompleted && trace.BlockNumber >= _range.EndBlock)
        {
            _rangeCompleted = true;

            if (_logger.IsInfo)
            {
                _logger.Info($"RealTime: configured range {_range.StartBlock}-{_range.EndBlock} completed. Continuing to trace new blocks.");
            }

            // Write cumulative with completionStatus="complete" for initial range
            _ = _cumulativeWriter.FinalizeAsync(CreateCumulativeOutput("complete"), "complete");
        }
    }

    /// <summary>
    /// Creates a per-block trace output from block trace data.
    /// </summary>
    private PerBlockTraceOutput CreatePerBlockOutput(OpcodeBlockTrace trace)
    {
        return new PerBlockTraceOutput
        {
            Metadata = new PerBlockMetadata
            {
                BlockNumber = trace.BlockNumber,
                ParentHash = trace.ParentHash,
                Timestamp = (long)trace.Timestamp,
                TransactionCount = trace.TransactionCount,
                GasUsed = null, // Not available in current OpcodeBlockTrace
                TracedAt = DateTime.UtcNow
            },
            OpcodeCounts = new Dictionary<string, long>(trace.Opcodes)
        };
    }

    /// <summary>
    /// Creates a cumulative trace output with current state.
    /// </summary>
    private CumulativeTraceOutput CreateCumulativeOutput(string completionStatus)
    {
        return new CumulativeTraceOutput
        {
            Metadata = new CumulativeMetadata
            {
                FirstBlock = _firstBlock >= 0 ? _firstBlock : _range.StartBlock,
                LastBlock = _lastBlock >= 0 ? _lastBlock : _range.StartBlock,
                TotalBlocksProcessed = _totalBlocksProcessed,
                SessionId = _sessionId,
                Mode = "RealTime",
                StartedAt = _startedAt,
                LastUpdatedAt = DateTime.UtcNow,
                Duration = _stopwatch.ElapsedMilliseconds,
                CompletionStatus = completionStatus,
                ConfiguredStartBlock = _range.StartBlock,
                ConfiguredEndBlock = _range.EndBlock
            },
            OpcodeCounts = _counter.ToOpcodeCountsDictionary()
        };
    }

    /// <summary>
    /// Updates the cumulative file asynchronously.
    /// </summary>
    private async Task UpdateCumulativeFileAsync()
    {
        try
        {
            string status = _rangeCompleted ? "complete" : "in_progress";
            CumulativeTraceOutput output = CreateCumulativeOutput(status);
            await _cumulativeWriter.WriteAsync(output).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
            {
                _logger.Error($"Failed to update cumulative file: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Finalizes the tracer with partial completion status.
    /// Called during graceful shutdown.
    /// </summary>
    public async Task FinalizePartialAsync()
    {
        _stopwatch.Stop();

        if (_logger.IsInfo)
        {
            _logger.Info($"Finalizing RealTime tracer: {_totalBlocksProcessed} blocks processed");
        }

        // Flush pending per-block writes
        await _writeQueue.FlushAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Write final cumulative with partial status
        await _cumulativeWriter.FinalizeAsync(CreateCumulativeOutput("partial"), "partial").ConfigureAwait(false);
    }

    /// <summary>
    /// Converts an opcode name back to its byte value.
    /// </summary>
    private static byte GetOpcodeByteFromName(string opcodeName)
    {
        // Handle hex format like "0xfe"
        if (opcodeName.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToByte(opcodeName, 16);
        }

        // Try to parse as Instruction enum
        if (Enum.TryParse<Nethermind.Evm.Instruction>(opcodeName, out var instruction))
        {
            return (byte)instruction;
        }

        return 0; // Unknown opcode
    }

    /// <summary>
    /// Asynchronously disposes of the tracer resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _writeQueue.DisposeAsync().ConfigureAwait(false);
    }
}
