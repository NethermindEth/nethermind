// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing.Parallel;

/// <summary>
/// Shared context between <see cref="StateDiffScopeProviderDecorator"/> and
/// <see cref="ParallelBlockValidationTransactionsExecutor"/>. The decorator feeds
/// it with scope information; the executor buffers diffs that get injected when
/// the next write batch is created (via <c>WorldState.Commit(commitRoots: true)</c>).
/// </summary>
public class ParallelBlockExecutionContext
{
    private List<TransactionStateDiff>? _pendingDiffs;

    /// <summary>
    /// The base block header captured from the last <see cref="Evm.State.IWorldStateScopeProvider.BeginScope"/> call.
    /// </summary>
    public BlockHeader? LastBaseBlock { get; set; }

    /// <summary>
    /// Buffer a diff for deferred injection into the next write batch.
    /// </summary>
    public void BufferDiff(TransactionStateDiff diff) =>
        (_pendingDiffs ??= []).Add(diff);

    /// <summary>
    /// Returns and clears all pending diffs. Called by the decorator's
    /// <c>StartWriteBatch</c> to inject them into the write batch.
    /// </summary>
    public List<TransactionStateDiff>? TakePendingDiffs()
    {
        List<TransactionStateDiff>? diffs = _pendingDiffs;
        _pendingDiffs = null;
        return diffs;
    }
}
