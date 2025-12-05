// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.OpcodeTracing.Plugin;

/// <summary>
/// Tracks progress during long-running tracing operations.
/// </summary>
public sealed class TracingProgress
{
    private long _currentBlock;
    private readonly long _startBlock;
    private readonly long _totalBlocks;
    private readonly DateTime _startTime;
    private long _lastLoggedBlock;

    /// <summary>
    /// Initializes a new instance of the <see cref="TracingProgress"/> class.
    /// </summary>
    /// <param name="startBlock">The first block number in the range.</param>
    /// <param name="endBlock">The last block number in the range.</param>
    public TracingProgress(long startBlock, long endBlock)
    {
        _startBlock = startBlock;
        _currentBlock = startBlock - 1;
        _totalBlocks = endBlock - startBlock + 1;
        _startTime = DateTime.UtcNow;
        _lastLoggedBlock = startBlock - 1;
    }

    /// <summary>
    /// Gets the last fully processed block number.
    /// </summary>
    public long CurrentBlock => Interlocked.Read(ref _currentBlock);

    /// <summary>
    /// Gets the total number of blocks to process.
    /// </summary>
    public long TotalBlocks => _totalBlocks;

    /// <summary>
    /// Gets the tracing start timestamp (UTC).
    /// </summary>
    public DateTime StartTime => _startTime;

    /// <summary>
    /// Gets the elapsed time in milliseconds since tracing started.
    /// </summary>
    public long ElapsedMilliseconds => (long)(DateTime.UtcNow - _startTime).TotalMilliseconds;

    /// <summary>
    /// Gets the completion percentage (0-100).
    /// </summary>
    public double PercentComplete => ((double)(CurrentBlock - _startBlock + 1) / _totalBlocks) * 100;

    /// <summary>
    /// Updates the current block progress.
    /// </summary>
    /// <param name="blockNumber">The block number that was just completed.</param>
    public void UpdateProgress(long blockNumber)
    {
        Interlocked.Exchange(ref _currentBlock, blockNumber);
    }

    /// <summary>
    /// Determines whether progress should be logged based on milestone thresholds (every 1000 blocks).
    /// </summary>
    /// <returns>True if progress should be logged; otherwise, false.</returns>
    public bool ShouldLogProgress()
    {
        long current = CurrentBlock;
        long lastLogged = Interlocked.Read(ref _lastLoggedBlock);

        if (current - lastLogged >= 1000)
        {
            Interlocked.Exchange(ref _lastLoggedBlock, current);
            return true;
        }

        return false;
    }
}
