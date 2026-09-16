// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Tracing;

/// <summary>Traces every transaction of a covered block at once, each on the exact state before it as the
/// transaction index supplies it, and returns the traces in block order. False means the block is not covered and
/// the caller replays it as before.</summary>
public interface IParallelBlockTracer
{
    /// <param name="forTransaction">Builds the tracer for one transaction over the world state it will run in.</param>
    /// <param name="afterTransactions">Builds the tracer for what follows the transactions, the rewards, run once with
    /// the seed for the end of the block armed throughout, so it sees the state the last transaction left; null when
    /// nothing follows.</param>
    bool TryTrace<TTrace>(
        Block block,
        BlockHeader parent,
        Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction,
        Func<IWorldState, IBlockTracer<TTrace>>? afterTransactions,
        CancellationToken token,
        [NotNullWhen(true)] out IReadOnlyList<TTrace>? traces);
}
