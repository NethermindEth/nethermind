// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using System;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Maintain a pool of <see cref="IReadOnlyTxProcessorSource"/> that is repopulated when the <see cref="IReadOnlyTxProcessingScope"/>
/// returned by <see cref="Build"/> is disposed. This provide a convenient thread safe helper to get an <see cref="IReadOnlyTxProcessingScope"/>
/// without having to track the corresponding source.
/// </summary>
/// <param name="envFactory"></param>
public class ShareableTxProcessingSource(IReadOnlyTxProcessingEnvFactory envFactory) : IShareableTxProcessorSource
{
    ObjectPool<IReadOnlyTxProcessorSource> _envPool = new DefaultObjectPoolProvider().Create(new EnvPoolPolicy(envFactory));

    public IReadOnlyTxProcessingScope Build(BlockHeader? baseBlock)
    {
        IReadOnlyTxProcessorSource? source = _envPool.Get();
        IReadOnlyTxProcessingScope? scope = source.Build(baseBlock);
        return new ScopeWrapper(source, _envPool, scope);
    }

    public void Dispose() => (_envPool as IDisposable)?.Dispose();

    private class EnvPoolPolicy(IReadOnlyTxProcessingEnvFactory envFactory) : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        public IReadOnlyTxProcessorSource Create()
        {
            return envFactory.Create();
        }

        public bool Return(IReadOnlyTxProcessorSource obj)
        {
            return true;
        }
    }

    private class ScopeWrapper(IReadOnlyTxProcessorSource source, ObjectPool<IReadOnlyTxProcessorSource> envPool, IReadOnlyTxProcessingScope scope) : IReadOnlyTxProcessingScope
    {
        private readonly IReadOnlyTxProcessingScope _scope = scope;
        private readonly IReadOnlyTxProcessorSource _source = source;
        private readonly ObjectPool<IReadOnlyTxProcessorSource> _envPool = envPool;

        public void Dispose()
        {
            _scope.Dispose();
            _envPool.Return(_source);
        }

        public ITransactionProcessor TransactionProcessor => _scope.TransactionProcessor;

        public IWorldState WorldState => _scope.WorldState;
    }
}
