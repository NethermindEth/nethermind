// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Maintain a pool of <see cref="IReadOnlyTxProcessorSource"/> that is repopulated when the <see cref="IReadOnlyTxProcessingScope"/>
/// returned by <see cref="Build"/> is disposed. This provide a convenient thread safe helper to get an <see cref="IReadOnlyTxProcessingScope"/>
/// without having to track the corresponding source.
/// </summary>
/// <param name="envFactory"></param>
public class ShareableTxProcessingSource(IReadOnlyTxProcessingEnvFactory envFactory) : IShareableTxProcessorSource
{
    ObjectPool<IReadOnlyTxProcessorSource> _envPool = new DefaultObjectPool<IReadOnlyTxProcessorSource>(new EnvPoolPolicy(envFactory));

    public IReadOnlyTxProcessingScope Build(BlockHeader? baseBlock)
    {
        IReadOnlyTxProcessorSource? source = _envPool.Get();
        IReadOnlyTxProcessingScope? scope = source.Build(baseBlock);
        return new ScopeWrapper(source, _envPool, scope);
    }

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

    private class ScopeWrapper : IReadOnlyTxProcessingScope
    {
        private readonly IReadOnlyTxProcessingScope _scope;
        private readonly IReadOnlyTxProcessorSource _source;
        private readonly ObjectPool<IReadOnlyTxProcessorSource> _envPool2;

        public ScopeWrapper(IReadOnlyTxProcessorSource source, ObjectPool<IReadOnlyTxProcessorSource> envPool2, IReadOnlyTxProcessingScope scope)
        {
            _scope = scope;
            _source = source;
            _envPool2 = envPool2;
        }

        public void Dispose()
        {
            _scope.Dispose();
            _envPool2.Return(_source);
        }

        public ITransactionProcessor TransactionProcessor => _scope.TransactionProcessor;

        public IVisitingWorldState WorldState => _scope.WorldState;
    }
}
