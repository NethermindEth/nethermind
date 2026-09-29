// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing;

/// <summary>
/// The block-execution capabilities a build compiles in.
/// </summary>
/// <remarks>
/// Readers test a flag before the run-time decision it guards, so a constant <c>true</c> folds
/// away and this build pays nothing. It exists so the zkEVM build can answer <c>false</c>: an
/// ahead-of-time compiler emits every reachable path, and a guest that runs single-threaded would
/// otherwise carry the parallel executor, its worker pool and the thread-pool machinery behind
/// them. See <c>ExecutionFlags.zkevm.cs</c>.
/// </remarks>
internal static partial class ExecutionFlags
{
    /// <summary>Whether this build can execute a block's transactions in parallel.</summary>
    public static bool ParallelExecution => true;
}
