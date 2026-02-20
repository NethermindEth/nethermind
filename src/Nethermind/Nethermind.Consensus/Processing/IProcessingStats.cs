// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Interface for processing statistics tracking during block processing.
/// </summary>
public interface IProcessingStats
{
    /// <summary>
    /// Event fired when new processing statistics are available.
    /// </summary>
    event EventHandler<BlockStatistics>? NewProcessingStatistics;

    /// <summary>
    /// Start the statistics timer.
    /// </summary>
    void Start();

    /// <summary>
    /// Capture the starting values of metrics before processing begins.
    /// </summary>
    void CaptureStartStats();

    /// <summary>
    /// Update statistics after a block has been processed.
    /// </summary>
    /// <param name="block">The processed block.</param>
    /// <param name="baseBlock">The parent block header.</param>
    /// <param name="blockProcessingTimeInMicros">Processing time in microseconds.</param>
    void UpdateStats(Block? block, BlockHeader? baseBlock, long blockProcessingTimeInMicros);
}
