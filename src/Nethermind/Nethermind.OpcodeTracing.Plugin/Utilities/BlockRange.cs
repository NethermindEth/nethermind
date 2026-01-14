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
}
