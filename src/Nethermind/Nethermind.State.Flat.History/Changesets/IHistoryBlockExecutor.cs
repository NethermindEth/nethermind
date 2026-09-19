// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Re-executes canonical blocks against the state of their parent. The state layer knows what it wants
/// traced; how a block is found and processed belongs above it. One executor serves one thread at a time.</summary>
public interface IHistoryBlockExecutor : IDisposable
{
    /// <summary>False when the block is not available to execute, which is a reason to wait rather than to fail.</summary>
    bool TryExecute(ulong block, IBlockTracer tracer, CancellationToken cancellationToken);

    /// <summary>Opens the state once at the parent of <paramref name="firstBlock"/> for a run of consecutive blocks,
    /// so what one block writes is what the next one reads from memory instead of from history. Null when the
    /// parent is not available.</summary>
    IHistoryBlockRun? BeginRun(ulong firstBlock);
}

/// <summary>One caller-owned sequential replay scope. Dispose before reusing its executor.</summary>
public interface IHistoryBlockRun : IDisposable
{
    /// <summary>Executes the next block of the run, ascending. False when it is not available; the run is then over.</summary>
    bool TryExecuteNext(IBlockTracer tracer, CancellationToken cancellationToken);
}

/// <summary>Creates isolated executors; each executor and its runs belong to one worker.</summary>
public interface IHistoryBlockExecutorFactory
{
    /// <summary>Returns a caller-owned executor, which must be disposed after its final run.</summary>
    IHistoryBlockExecutor Create();
    /// <summary>Returns the last supported canonical height at or below the bound, or null when it cannot be determined.</summary>
    ulong? GetLastSupportedBlock(ulong lowerBound, ulong upperBound) => lowerBound <= upperBound ? upperBound : null;
}
