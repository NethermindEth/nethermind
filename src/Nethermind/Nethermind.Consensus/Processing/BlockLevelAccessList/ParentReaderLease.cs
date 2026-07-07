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
/// Used by parallel workers so each tx gets its own snapshot reader without contending on the
/// mutable state provider.
/// </summary>
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
