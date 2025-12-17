// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Channels;
using Nethermind.Logging;

namespace Nethermind.OpcodeTracing.Plugin.Output;

/// <summary>
/// Asynchronous file write queue to prevent blocking the main block processing thread.
/// Ensures per-block file writes have &lt;100ms latency per FR-005g and FR-022.
/// </summary>
public sealed class AsyncFileWriteQueue : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly PerBlockTraceWriter _perBlockWriter;
    private readonly string _outputDirectory;
    private readonly Channel<PerBlockTraceOutput> _writeChannel;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Task _processingTask;
    private int _isCompleted;

    /// <summary>
    /// Gets the number of pending writes in the queue.
    /// </summary>
    public int PendingWrites => _writeChannel.Reader.Count;

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncFileWriteQueue"/> class.
    /// </summary>
    /// <param name="outputDirectory">The output directory for per-block files.</param>
    /// <param name="perBlockWriter">The per-block trace writer.</param>
    /// <param name="logManager">The log manager.</param>
    public AsyncFileWriteQueue(string outputDirectory, PerBlockTraceWriter perBlockWriter, ILogManager logManager)
    {
        _outputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
        _perBlockWriter = perBlockWriter ?? throw new ArgumentNullException(nameof(perBlockWriter));
        _logger = logManager?.GetClassLogger<AsyncFileWriteQueue>() ?? throw new ArgumentNullException(nameof(logManager));

        // Unbounded channel to avoid blocking producers
        _writeChannel = Channel.CreateUnbounded<PerBlockTraceOutput>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _cancellationTokenSource = new CancellationTokenSource();
        _processingTask = ProcessQueueAsync(_cancellationTokenSource.Token);
    }

    /// <summary>
    /// Enqueues a per-block trace output for asynchronous writing.
    /// This method returns immediately without blocking.
    /// </summary>
    /// <param name="traceOutput">The per-block trace output to write.</param>
    /// <returns>True if the item was enqueued; false if the queue is closed.</returns>
    public bool Enqueue(PerBlockTraceOutput traceOutput)
    {
        if (traceOutput is null)
        {
            return false;
        }

        return _writeChannel.Writer.TryWrite(traceOutput);
    }

    /// <summary>
    /// Processes the write queue asynchronously.
    /// </summary>
    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (PerBlockTraceOutput traceOutput in _writeChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await _perBlockWriter.WriteBlockAsync(_outputDirectory, traceOutput).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Log error but continue processing other items
                    if (_logger.IsError)
                    {
                        _logger.Error($"Error writing per-block trace for block {traceOutput.Metadata.BlockNumber}: {ex.Message}", ex);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
            if (_logger.IsDebug)
            {
                _logger.Debug("Async file write queue processing cancelled");
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
            {
                _logger.Error($"Async file write queue processing failed: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Flushes all pending writes and waits for completion.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for pending writes.</param>
    /// <returns>True if all writes completed; false if timeout occurred.</returns>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        // Signal no more writes will be added (only once)
        if (Interlocked.Exchange(ref _isCompleted, 1) == 0)
        {
            _writeChannel.Writer.TryComplete();
        }

        // Wait for processing to complete with timeout
        using var timeoutCts = new CancellationTokenSource(timeout);

        try
        {
            await _processingTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsWarn)
            {
                _logger.Warn($"Flush timeout after {timeout.TotalSeconds}s, {PendingWrites} writes may be lost");
            }
            return false;
        }
    }

    /// <summary>
    /// Asynchronously disposes of the queue resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_logger.IsDebug)
        {
            _logger.Debug($"Disposing async file write queue, {PendingWrites} pending writes");
        }

        // Give pending writes time to complete (5 seconds max)
        // FlushAsync handles completing the channel with thread-safety
        await FlushAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Cancel any remaining processing
        await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);

        try
        {
            await _processingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        _cancellationTokenSource.Dispose();
    }
}
