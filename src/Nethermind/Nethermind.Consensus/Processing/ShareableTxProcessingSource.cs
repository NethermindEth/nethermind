// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Cpu;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Maintain a pool of <see cref="IReadOnlyTxProcessorSource"/> that is repopulated when the <see cref="IReadOnlyTxProcessingScope"/>
/// returned by <see cref="Build"/> is disposed. This provide a convenient thread safe helper to get an <see cref="IReadOnlyTxProcessingScope"/>
/// without having to track the corresponding source.
/// </summary>
/// <param name="envFactory"></param>
public class ShareableTxProcessingSource(IReadOnlyTxProcessingEnvFactory envFactory) : IShareableTxProcessorSource
{
    // Scales with cores to stay warm under load; absolute cap prevents 64+ core hosts from allocating ~1 GB+ of pooled envs.
    private const int MaxRetainedAbsoluteCap = 256;
    ObjectPool<IReadOnlyTxProcessorSource> _envPool =
        new DefaultObjectPoolProvider { MaximumRetained = Math.Min(RuntimeInformation.ProcessorCount * 16, MaxRetainedAbsoluteCap) }
            .Create(new EnvPoolPolicy(envFactory));

    public bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
    {
        IReadOnlyTxProcessorSource source = _envPool.Get();
        if (!source.TryBuild(baseBlock, out IReadOnlyTxProcessingScope? inner))
        {
            _envPool.Return(source);
            scope = null;
            return false;
        }

        scope = new ScopeWrapper(source, _envPool, inner);
        return true;
    }

    public void Dispose() => (_envPool as IDisposable)?.Dispose();

    private class EnvPoolPolicy(IReadOnlyTxProcessingEnvFactory envFactory) : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        public IReadOnlyTxProcessorSource Create() => envFactory.Create();

        public bool Return(IReadOnlyTxProcessorSource obj) => true;
    }

    private class ScopeWrapper(IReadOnlyTxProcessorSource source, ObjectPool<IReadOnlyTxProcessorSource> envPool, IReadOnlyTxProcessingScope scope) : IReadOnlyTxProcessingScope
    {
        private readonly IReadOnlyTxProcessingScope _scope = scope;
        private readonly IReadOnlyTxProcessorSource _source = source;
        private readonly ObjectPool<IReadOnlyTxProcessorSource> _envPool = envPool;

        public void Dispose()
        {
            try { _scope.Dispose(); }
            finally { _envPool.Return(_source); }
        }

        public ITransactionProcessor TransactionProcessor => _scope.TransactionProcessor;

        public IWorldState WorldState => _scope.WorldState;
    }
}
