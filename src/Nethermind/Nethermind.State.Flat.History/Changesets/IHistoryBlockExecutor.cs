// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Re-executes a canonical block against the state of its parent. The state layer knows what it wants
/// traced; how a block is found and processed belongs above it.</summary>
public interface IHistoryBlockExecutor
{
    /// <summary>False when the block is not available to execute, which is a reason to wait rather than to fail.</summary>
    bool TryExecute(ulong block, IBlockTracer tracer, CancellationToken cancellationToken);
}
