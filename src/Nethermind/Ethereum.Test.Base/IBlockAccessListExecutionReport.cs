// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Ethereum.Test.Base;

/// <summary>
/// Told which executor runs each fixture block, and when a block the parallel executor rejected is re-run
/// sequentially; <see cref="SequentialRetryReport"/> observes the re-run, the node does not report it.
/// </summary>
public interface IBlockAccessListExecutionReport
{
    /// <param name="block">The block about to be executed.</param>
    /// <param name="sequentialReason">Null when the parallel executor runs the block; otherwise the first condition that ruled it out.</param>
    void OnExecutionPathChosen(Block block, string? sequentialReason);

    /// <param name="block">The block the parallel executor rejected.</param>
    /// <param name="parallelError">Why the parallel executor rejected it.</param>
    /// <param name="sequentialError">Why the sequential re-run rejected it, or null when it accepted the block.</param>
    void OnSequentialRetry(Block block, Exception parallelError, Exception? sequentialError);
}
