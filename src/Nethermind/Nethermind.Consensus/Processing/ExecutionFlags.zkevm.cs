// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing;

/// <inheritdoc cref="ExecutionFlags"/>
internal static partial class ExecutionFlags
{
    /// <summary>The guest runs single-threaded, so readers fold the parallel paths out of the image.</summary>
    public static bool ParallelExecution => false;
}
