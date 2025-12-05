// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.OpcodeTracing.Plugin.Utilities;

/// <summary>
/// Represents an inclusive block range for tracing operations.
/// </summary>
public readonly record struct BlockRange
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BlockRange"/> struct.
    /// </summary>
    /// <param name="startBlock">The first block number (inclusive).</param>
    /// <param name="endBlock">The last block number (inclusive).</param>
    /// <exception cref="ArgumentException">Thrown when end block is less than start block or start block is negative.</exception>
    public BlockRange(long startBlock, long endBlock)
    {
        if (endBlock < startBlock)
        {
            throw new ArgumentException($"EndBlock ({endBlock}) must be >= StartBlock ({startBlock})");
        }

        if (startBlock < 0)
        {
            throw new ArgumentException("StartBlock must be non-negative");
        }

        StartBlock = startBlock;
        EndBlock = endBlock;
    }

    /// <summary>
    /// Gets the first block number in the range (inclusive).
    /// </summary>
    public long StartBlock { get; }

    /// <summary>
    /// Gets the last block number in the range (inclusive).
    /// </summary>
    public long EndBlock { get; }

    /// <summary>
    /// Gets the total number of blocks in this range.
    /// </summary>
    public long Count => EndBlock - StartBlock + 1;

    /// <summary>
    /// Determines whether the specified block number is within this range.
    /// </summary>
    /// <param name="blockNumber">The block number to check.</param>
    /// <returns>True if the block number is within the range; otherwise, false.</returns>
    public bool Contains(long blockNumber) => blockNumber >= StartBlock && blockNumber <= EndBlock;

    /// <summary>
    /// Determines whether this range intersects with another range.
    /// </summary>
    /// <param name="other">The other block range.</param>
    /// <returns>True if the ranges overlap; otherwise, false.</returns>
    public bool Intersects(BlockRange other) => StartBlock <= other.EndBlock && EndBlock >= other.StartBlock;

    /// <summary>
    /// Splits this range into approximately equal sub-ranges.
    /// </summary>
    /// <param name="chunks">The number of chunks to split into.</param>
    /// <returns>An array of block ranges.</returns>
    /// <exception cref="ArgumentException">Thrown when chunks is less than or equal to zero.</exception>
    public BlockRange[] Split(int chunks)
    {
        if (chunks <= 0)
        {
            throw new ArgumentException("Chunks must be positive", nameof(chunks));
        }

        if (chunks == 1)
        {
            return [this];
        }

        long blocksPerChunk = Count / chunks;
        long remainder = Count % chunks;
        var ranges = new BlockRange[chunks];

        long currentStart = StartBlock;
        for (int i = 0; i < chunks; i++)
        {
            long chunkSize = blocksPerChunk + (i < remainder ? 1 : 0);
            long currentEnd = currentStart + chunkSize - 1;
            ranges[i] = new BlockRange(currentStart, Math.Min(currentEnd, EndBlock));
            currentStart = currentEnd + 1;
        }

        return ranges;
    }
}
