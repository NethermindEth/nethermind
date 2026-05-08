// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
    /// Update statistics after blocks have been processed.
    /// </summary>
    /// <param name="blocks">The processed blocks.</param>
    /// <param name="baseBlock">The parent block header.</param>
    /// <param name="blockProcessingTimeInMicros">Processing time in microseconds.</param>
    /// <remarks>
    /// Uses <see cref="IReadOnlyList{T}"/> rather than <see cref="ReadOnlySpan{T}"/> so that
    /// dynamic-proxy mocks (NSubstitute, Moq) can generate proxies — ref-struct parameters
    /// break dynamic proxy generation, which in turn aborts block processing in tests.
    /// </remarks>
    void UpdateStats(IReadOnlyList<Block> blocks, BlockHeader? baseBlock, long blockProcessingTimeInMicros);
}
