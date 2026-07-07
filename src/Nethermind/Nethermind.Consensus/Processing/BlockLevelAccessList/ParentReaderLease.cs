// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>
/// RAII wrapper around a borrowed read-only tx-processing env: holds the pooled source plus the
/// scope built against the parent state root, and returns the source to its pool when disposed.
/// </summary>
/// <remarks>
/// Rented by <see cref="ParallelBalEnvManager"/> and handed to <see cref="ParallelBalEnv"/> via
/// <see cref="IBalProcessingEnv.Setup"/>, so each parallel worker reads from its own parent-state
/// snapshot without contending on the mutable state provider. Public only because it appears on the
/// public <see cref="IBalProcessingEnv"/> contract.
/// </remarks>
public sealed class ParentReaderLease(
    IReadOnlyTxProcessorSource source,
    ObjectPool<IReadOnlyTxProcessorSource> envPool,
    IReadOnlyTxProcessingScope scope) : IDisposable
{
    private IReadOnlyTxProcessorSource? _source = source;
    private IReadOnlyTxProcessingScope? _scope = scope;

    public IWorldState WorldState => _scope?.WorldState ?? ThrowDisposed();

    public void Dispose()
    {
        IReadOnlyTxProcessingScope? scope = _scope;
        IReadOnlyTxProcessorSource? src = _source;
        _scope = null;
        _source = null;
        scope?.Dispose();
        if (src is not null) envPool.Return(src);
    }

    [DoesNotReturn]
    private static IWorldState ThrowDisposed()
        => throw new ObjectDisposedException(nameof(ParentReaderLease));
}
