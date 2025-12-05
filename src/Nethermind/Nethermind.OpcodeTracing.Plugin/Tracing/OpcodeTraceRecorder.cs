// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Output;
using Nethermind.OpcodeTracing.Plugin.Utilities;

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

    private TraceConfiguration? _traceConfig;
    private OpcodeBlockTracer? _blockTracer;
    private RealTimeTracer? _realTimeTracer;
    private TracingProgress? _progress;
    private Stopwatch? _stopwatch;
    private CancellationTokenSource? _cts;
    private Task? _tracingTask;
    private long _lastProcessedBlock;
    private bool _isComplete;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpcodeTraceRecorder"/> class.
    /// </summary>
    /// <param name="config">The opcode tracing configuration.</param>
    /// <param name="counter">The opcode counter.</param>
    /// <param name="outputWriter">The trace output writer.</param>
    /// <param name="logManager">The log manager.</param>
    public OpcodeTraceRecorder(
        IOpcodeTracingConfig config,
        OpcodeCounter counter,
        TraceOutputWriter outputWriter,
        ILogManager logManager)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _outputWriter = outputWriter ?? throw new ArgumentNullException(nameof(outputWriter));
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

            // Validate configuration
            var validationResult = BlockRangeValidator.Validate(_config, currentChainTip);
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
            var range = new BlockRange(_traceConfig.EffectiveStartBlock, _traceConfig.EffectiveEndBlock);
            _realTimeTracer = new RealTimeTracer(_counter, range, OnBlockCompletedRealTime, api.LogManager);

            _blockTracer = new OpcodeBlockTracer(_realTimeTracer.OnBlockCompleted);
            processingContext.BlockchainProcessor.Tracers.Add(_blockTracer);

            _stopwatch = Stopwatch.StartNew();

            if (_logger.IsInfo)
            {
                _logger.Info("Opcode tracing attached to block processor (RealTime mode)");
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
        if (_traceConfig is not null && blockNumber >= _traceConfig.EffectiveEndBlock)
        {
            _isComplete = true;
            Task.Run(async () => await WriteOutputAsync().ConfigureAwait(false));
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
        _cts?.Cancel();
        _cts?.Dispose();
        _tracingTask?.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Asynchronously disposes of the tracer resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
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
            }
            _cts.Dispose();
        }
    }
}
