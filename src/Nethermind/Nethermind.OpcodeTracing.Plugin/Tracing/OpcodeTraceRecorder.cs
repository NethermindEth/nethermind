// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Output;
using Nethermind.OpcodeTracing.Plugin.Utilities;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.OpcodeTracing.Plugin.Tracing;

/// <summary>
/// Orchestrates opcode tracing operations across block ranges.
/// </summary>
public sealed class OpcodeTraceRecorder : IDisposable, IAsyncDisposable
{
    private readonly IOpcodeTracingConfig _config;
    private readonly ILogger _logger;
    private readonly OpcodeCounter _counter;
    private readonly TraceOutputWriter _outputWriter;
    private readonly string _sessionId;

    private TraceConfiguration? _traceConfig;
    private OpcodeBlockTracer? _blockTracer;
    private RealTimeTracer? _realTimeTracer;
    private TracingProgress? _progress;
    private Stopwatch? _stopwatch;
    private CancellationTokenSource? _cts;
    private Task? _tracingTask;
    private long _lastProcessedBlock;
    private bool _isComplete;
    private ISyncModeSelector? _syncModeSelector;
    private bool _syncModeWarningLogged;
    private bool _syncCompleteLogged;
    private bool _waitingForBlockLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpcodeTraceRecorder"/> class.
    /// </summary>
    /// <param name="config">The opcode tracing configuration.</param>
    /// <param name="counter">The opcode counter.</param>
    /// <param name="outputWriter">The trace output writer.</param>
    /// <param name="sessionId">The unique session identifier for RealTime mode cumulative file naming per FR-005d.</param>
    /// <param name="logManager">The log manager.</param>
    public OpcodeTraceRecorder(
        IOpcodeTracingConfig config,
        OpcodeCounter counter,
        TraceOutputWriter outputWriter,
        string sessionId,
        ILogManager logManager)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _outputWriter = outputWriter ?? throw new ArgumentNullException(nameof(outputWriter));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _logger = logManager?.GetClassLogger<OpcodeTraceRecorder>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    /// <summary>
    /// Prepares the tracer for operation by validating configuration and initializing resources.
    /// </summary>
    /// <param name="api">The Nethermind API.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task PrepareAsync(INethermindApi api, CancellationToken cancellationToken = default)
    {
        if (api is null)
        {
            throw new ArgumentNullException(nameof(api));
        }

        try
        {
            // Get current chain tip
            long currentChainTip = api.BlockTree?.Head?.Number ?? 0;

            // Parse mode for validation
            TracingMode mode = TracingMode.RealTime;
            if (!string.IsNullOrEmpty(_config.Mode) &&
                Enum.TryParse<TracingMode>(_config.Mode, ignoreCase: true, out var parsedMode))
            {
                mode = parsedMode;
            }

            // Validate configuration (mode-aware: Retrospective can wait for blocks during sync)
            var validationResult = BlockRangeValidator.Validate(_config, currentChainTip, mode);
            if (validationResult.IsError)
            {
                if (_logger.IsError)
                {
                    _logger.Error($"Configuration validation failed: {validationResult.Message}");
                }
                throw new InvalidOperationException($"Invalid configuration: {validationResult.Message}");
            }

            if (validationResult.IsWarning && _logger.IsWarn)
            {
                _logger.Warn($"Configuration warning: {validationResult.Message}");
            }

            // Create trace configuration
            _traceConfig = TraceConfiguration.FromConfig(_config, currentChainTip);

            // Log warnings from configuration
            foreach (var warning in _traceConfig.Warnings)
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn(warning);
                }
            }

            // Validate output directory
            if (!DirectoryHelper.ValidateWritable(_traceConfig.OutputDirectory))
            {
                throw new InvalidOperationException($"Output directory is not writable: {_traceConfig.OutputDirectory}");
            }

            // Initialize progress tracker
            _progress = new TracingProgress(_traceConfig.EffectiveStartBlock, _traceConfig.EffectiveEndBlock);
            _lastProcessedBlock = _traceConfig.EffectiveStartBlock - 1;

            if (_logger.IsInfo)
            {
                _logger.Info($"Opcode tracing prepared: blocks {_traceConfig.EffectiveStartBlock}-{_traceConfig.EffectiveEndBlock}, mode: {_traceConfig.Mode}");
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
            {
                _logger.Error($"Failed to prepare opcode tracing: {ex.Message}", ex);
            }
            throw;
        }
    }

    /// <summary>
    /// Attaches the tracer to the block processing pipeline for real-time mode.
    /// </summary>
    /// <param name="api">The Nethermind API.</param>
    public void Attach(INethermindApi api)
    {
        if (api is null)
        {
            throw new ArgumentNullException(nameof(api));
        }

        if (_traceConfig is null)
        {
            if (_logger.IsWarn)
            {
                _logger.Warn("Cannot attach tracer: configuration not prepared");
            }
            return;
        }

        if (_traceConfig.Mode != TracingMode.RealTime)
        {
            if (_logger.IsDebug)
            {
                _logger.Debug("Skipping tracer attachment: not in RealTime mode");
            }
            return;
        }

        var processingContext = api.MainProcessingContext;
        if (processingContext is null)
        {
            if (_logger.IsWarn)
            {
                _logger.Warn("Cannot attach tracer: processing context not available");
            }
            return;
        }

        try
        {
            // Check and log sync state - RealTime mode only captures blocks processed through the EVM
            _syncModeSelector = api.SyncModeSelector;
            if (_syncModeSelector is not null)
            {
                LogSyncStateWarning(_syncModeSelector.Current);
                _syncModeSelector.Changed += OnSyncModeChanged;
            }

            // For RealTime mode with Blocks parameter, recalculate range based on current chain tip
            // This ensures we trace the NEXT N blocks from when the tracer attaches, not from init time
            long effectiveStart = _traceConfig.EffectiveStartBlock;
            long effectiveEnd = _traceConfig.EffectiveEndBlock;

            if (_config.Blocks.HasValue && !_config.StartBlock.HasValue && !_config.EndBlock.HasValue)
            {
                long currentTip = api.BlockTree?.Head?.Number ?? 0;
                effectiveStart = currentTip + 1;
                effectiveEnd = currentTip + _config.Blocks.Value;

                // Update progress tracker with new range
                _progress = new TracingProgress(effectiveStart, effectiveEnd);

                if (_logger.IsInfo)
                {
                    _logger.Info($"RealTime mode with Blocks={_config.Blocks.Value}: recalculated range to {effectiveStart}-{effectiveEnd} (current tip: {currentTip})");
                }
            }

            var range = new BlockRange(effectiveStart, effectiveEnd);
            _realTimeTracer = new RealTimeTracer(
                _counter,
                range,
                _traceConfig.OutputDirectory,
                _sessionId,
                OnBlockCompletedRealTime,
                api.LogManager);

            _blockTracer = new OpcodeBlockTracer(_realTimeTracer.OnBlockCompleted);
            processingContext.BlockchainProcessor.Tracers.Add(_blockTracer);

            _stopwatch = Stopwatch.StartNew();

            if (_logger.IsInfo)
            {
                _logger.Info($"Opcode tracing attached to block processor (RealTime mode, session={_sessionId})");
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
            {
                _logger.Error($"Failed to attach tracer: {ex.Message}", ex);
            }
            throw;
        }
    }

    /// <summary>
    /// Logs a warning about sync state if the node is still syncing.
    /// RealTime mode only captures opcodes from blocks processed through the EVM,
    /// which doesn't happen during initial sync.
    /// </summary>
    private void LogSyncStateWarning(SyncMode syncMode)
    {
        if (_syncModeWarningLogged)
        {
            return;
        }

        bool isSyncing = !syncMode.NotSyncing();

        if (isSyncing && _logger.IsWarn)
        {
            _logger.Warn(
                $"RealTime opcode tracing is enabled, but the node is currently syncing (SyncMode={syncMode}). " +
                "RealTime mode only captures opcodes from NEW blocks processed at the chain tip AFTER sync completes. " +
                "Blocks downloaded during sync do NOT execute the EVM and will NOT be traced. " +
                "For tracing historical blocks during sync, use Retrospective mode instead: --OpcodeTracing.Mode Retrospective");
            _syncModeWarningLogged = true;
        }
        else if (!isSyncing && !_syncCompleteLogged && _logger.IsInfo)
        {
            _logger.Info($"Node sync complete (SyncMode={syncMode}). RealTime opcode tracing is now active for new blocks.");
            _syncCompleteLogged = true;
        }
    }

    /// <summary>
    /// Handles sync mode changes to log when RealTime tracing becomes active.
    /// </summary>
    private void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs args)
    {
        // Log transition to WaitingForBlock once - this is when RealTime tracing becomes effective
        if ((args.Current & SyncMode.WaitingForBlock) != 0 && !_waitingForBlockLogged && _logger.IsInfo)
        {
            // Use the actual range from the tracer (which may have been recalculated at attach time)
            var range = _realTimeTracer?.Range;
            _logger.Info(
                $"Sync state changed to {args.Current}. " +
                $"RealTime opcode tracing is now waiting for new blocks in range {range?.StartBlock ?? _traceConfig?.EffectiveStartBlock}-{range?.EndBlock ?? _traceConfig?.EffectiveEndBlock}.");
            _waitingForBlockLogged = true;
        }

        if (args.Current.NotSyncing() && !_syncCompleteLogged && _logger.IsInfo)
        {
            _logger.Info($"Node sync complete (SyncMode={args.Current}). RealTime opcode tracing is now active for new blocks.");
            _syncCompleteLogged = true;
        }
        else if (!args.Current.NotSyncing() && !_syncModeWarningLogged && _logger.IsWarn)
        {
            _logger.Warn($"Node entered sync mode (SyncMode={args.Current}). RealTime opcode tracing paused - only new blocks at chain tip are traced.");
            _syncModeWarningLogged = true;
        }
    }

    /// <summary>
    /// Executes retrospective tracing asynchronously.
    /// </summary>
    /// <param name="api">The Nethermind API.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task ExecuteTracingAsync(INethermindApi api)
    {
        if (api is null)
        {
            throw new ArgumentNullException(nameof(api));
        }

        if (_traceConfig is null || _progress is null)
        {
            if (_logger.IsError)
            {
                _logger.Error("Cannot execute tracing: configuration not prepared");
            }
            return Task.CompletedTask;
        }

        if (_traceConfig.Mode != TracingMode.Retrospective)
        {
            if (_logger.IsDebug)
            {
                _logger.Debug("Skipping retrospective execution: not in Retrospective mode");
            }
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _stopwatch = Stopwatch.StartNew();

        _tracingTask = Task.Run(async () =>
        {
            try
            {
                var blockTree = api.BlockTree;
                if (blockTree is null)
                {
                    if (_logger.IsError)
                    {
                        _logger.Error("BlockTree is not available");
                    }
                    return;
                }

                var range = new BlockRange(_traceConfig.EffectiveStartBlock, _traceConfig.EffectiveEndBlock);
                var tracer = new RetrospectiveTracer(blockTree, _counter, api.LogManager);

                if (_logger.IsInfo)
                {
                    _logger.Info($"Starting retrospective tracing of {range.Count} blocks");
                }

                await tracer.TraceBlockRangeAsync(range, _progress, _cts.Token).ConfigureAwait(false);

                _isComplete = true;
                _lastProcessedBlock = _traceConfig.EffectiveEndBlock;

                if (_logger.IsInfo)
                {
                    _logger.Info("Retrospective tracing completed");
                }
            }
            catch (OperationCanceledException)
            {
                _lastProcessedBlock = _progress.CurrentBlock;
                if (_logger.IsWarn)
                {
                    _logger.Warn($"Tracing interrupted at block {_lastProcessedBlock}");
                }
            }
            catch (Exception ex)
            {
                _lastProcessedBlock = _progress.CurrentBlock;
                if (_logger.IsError)
                {
                    _logger.Error($"Tracing failed: {ex.Message}", ex);
                }
            }
            finally
            {
                await WriteOutputAsync().ConfigureAwait(false);
            }
        }, _cts.Token);

        return _tracingTask;
    }

    private void OnBlockCompletedRealTime(long blockNumber)
    {
        _lastProcessedBlock = blockNumber;
        _progress?.UpdateProgress(blockNumber);

        if (_progress?.ShouldLogProgress() == true && _logger.IsInfo)
        {
            _logger.Info($"Real-time tracing progress: block {blockNumber} ({_progress.PercentComplete:F2}% complete)");
        }

        // Check if we've reached the end block
        // Note: RealTimeTracer handles writing the final cumulative file via CumulativeTraceWriter
        if (_traceConfig is not null && blockNumber >= _traceConfig.EffectiveEndBlock)
        {
            _isComplete = true;
        }
    }

    private async Task WriteOutputAsync()
    {
        if (_traceConfig is null)
        {
            return;
        }

        try
        {
            _stopwatch?.Stop();

            var metadata = new TraceMetadata
            {
                StartBlock = _traceConfig.EffectiveStartBlock,
                EndBlock = _isComplete ? _traceConfig.EffectiveEndBlock : _lastProcessedBlock,
                Mode = _traceConfig.Mode.ToString(),
                Timestamp = DateTime.UtcNow,
                Duration = _stopwatch?.ElapsedMilliseconds,
                CompletionStatus = _isComplete ? "complete" : "partial",
                Warnings = _traceConfig.Warnings.Count > 0 ? _traceConfig.Warnings.ToArray() : null
            };

            var opcodeCounts = _counter.ToOpcodeCountsDictionary();
            var traceOutput = new TraceOutput
            {
                Metadata = metadata,
                OpcodeCounts = opcodeCounts
            };

            string? outputPath = await _outputWriter.WriteAsync(_traceConfig.OutputDirectory, traceOutput).ConfigureAwait(false);
            if (outputPath is not null && _logger.IsInfo)
            {
                _logger.Info($"Opcode trace completed and written to: {outputPath}");
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
            {
                _logger.Error($"Failed to write trace output: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Disposes of the tracer resources.
    /// </summary>
    public void Dispose()
    {
        if (_syncModeSelector is not null)
        {
            _syncModeSelector.Changed -= OnSyncModeChanged;
        }
        _cts?.Cancel();
        _cts?.Dispose();
        _tracingTask?.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Asynchronously disposes of the tracer resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Unsubscribe from sync mode changes
        if (_syncModeSelector is not null)
        {
            _syncModeSelector.Changed -= OnSyncModeChanged;
        }

        // Cancel any running tasks
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_tracingTask is not null)
            {
                try
                {
                    await _tracingTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Task didn't complete in time
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }
            _cts.Dispose();
        }

        // Finalize RealTime tracer if active per FR-078
        if (_realTimeTracer is not null)
        {
            try
            {
                await _realTimeTracer.FinalizePartialAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_logger.IsError)
                {
                    _logger.Error($"Failed to finalize RealTime tracer: {ex.Message}", ex);
                }
            }

            await _realTimeTracer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
