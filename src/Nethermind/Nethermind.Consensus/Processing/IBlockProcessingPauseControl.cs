// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Controls pausing and resuming of the block processing loop. Kept separate from
/// <see cref="IBlockProcessingQueue"/> because pausing is a diagnostic/testing concern that the
/// vast majority of queue consumers neither need nor can support.
/// </summary>
public interface IBlockProcessingPauseControl
{
    /// <summary>
    /// Pauses dequeuing of blocks. A block already being processed finishes; the loop then parks
    /// before the next block. Blocks can still be enqueued and accumulate until <see cref="Resume"/>
    /// is called. Idempotent.
    /// </summary>
    void Pause();

    /// <summary>Resumes processing previously paused via <see cref="Pause"/>. No-op when not paused.</summary>
    void Resume();

    /// <summary>Whether block processing is currently paused.</summary>
    bool IsPaused { get; }
}
