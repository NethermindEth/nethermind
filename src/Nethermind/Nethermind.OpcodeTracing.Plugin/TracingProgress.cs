// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.OpcodeTracing.Plugin;

/// <summary>
/// Tracks progress during long-running tracing operations.
/// </summary>
public sealed class TracingProgress(ulong startBlock, ulong endBlock)
{
    private ulong _currentBlock = startBlock.SaturatingSub(1);
    private readonly ulong _startBlock = startBlock;
    private readonly ulong _totalBlocks = endBlock - startBlock + 1;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private ulong _lastLoggedBlock = startBlock - 1;

    /// <summary>
    /// Gets the last fully processed block number.
    /// </summary>
    public ulong CurrentBlock => Interlocked.Read(ref _currentBlock);

    /// <summary>
    /// Gets the total number of blocks to process.
    /// </summary>
    public ulong TotalBlocks => _totalBlocks;

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
    public void UpdateProgress(ulong blockNumber) =>
        Interlocked.Exchange(ref _currentBlock, blockNumber);

    /// <summary>
    /// Determines whether progress should be logged based on milestone thresholds (every 1000 blocks).
    /// </summary>
    /// <returns>True if progress should be logged; otherwise, false.</returns>
    public bool ShouldLogProgress()
    {
        ulong current = CurrentBlock;
        ulong lastLogged = Interlocked.Read(ref _lastLoggedBlock);

        if (current - lastLogged >= 1000)
        {
            Interlocked.Exchange(ref _lastLoggedBlock, current);
            return true;
        }

        return false;
    }
}
