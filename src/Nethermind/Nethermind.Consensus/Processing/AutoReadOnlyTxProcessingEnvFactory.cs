// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <param name="shareCodeCache">When false the env gets its own <see cref="ICodeCache"/> rather than the
/// process-wide one, which nothing journals, so a rolled-back deposit cannot outlive its scope.</param>
public class AutoReadOnlyTxProcessingEnvFactory(ILifetimeScope parentLifetime, IWorldStateManager worldStateManager, ISpecProvider specProvider, bool shareCodeCache = true) : IReadOnlyTxProcessingEnvFactory
{
    // A validation prefix touches few distinct hashes, and overflow only costs a re-read; an env without a
    // cache at all would re-copy the whole bytecode on every EXTCODE*/CALL* the prefix runs.
    private const int IsolatedCodeCacheCapacity = 512;

    public IReadOnlyTxProcessorSource Create()
    {
        IWorldStateScopeProvider worldState = worldStateManager.CreateResettableWorldState();
        // Mempool admission and the parallel BAL parent readers share these envs, so only add a recorder
        // where a diff can actually be read.
        IReleaseSpec finalSpec = specProvider.GetFinalSpec();
        bool recordsTransactionDiffs = finalSpec.IsEip7906Enabled && finalSpec.BlockLevelAccessListsEnabled;
        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope((builder) =>
        {
            builder
                .AddSingleton<IWorldStateScopeProvider>(worldState)
                .AddSingleton<AutoReadOnlyTxProcessingEnv>();
            if (!shareCodeCache)
            {
                builder.AddSingleton<ICodeCache>(new StaticCodeCache(IsolatedCodeCacheCapacity));
            }

            if (recordsTransactionDiffs)
            {
                // At scope level so the tx processor and the code repository share one slice.
                builder.AddDecorator<IWorldState>(static (_, inner) => new TracedAccessWorldState(inner, parallel: false));
            }
        });

        return childScope.Resolve<AutoReadOnlyTxProcessingEnv>();
    }

    public class AutoReadOnlyTxProcessingEnv(ITransactionProcessor transactionProcessor, IWorldState worldState, ILifetimeScope lifetimeScope) : IReadOnlyTxProcessorSource
    {
        public bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope) =>
            Wrap(worldState.TryBeginScope(baseBlock, out IDisposable? closer), closer, out scope);

        public bool TryBuildAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope) =>
            Wrap(worldState.TryBeginScopeAtTarget(targetBlock, out IDisposable? closer), closer, out scope);

        private bool Wrap(bool acquired, IDisposable? closer, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
        {
            scope = acquired ? new ReadOnlyTxProcessingScope(transactionProcessor, closer!, worldState) : null;
            return acquired;
        }

        public void Dispose() => lifetimeScope.Dispose();
    }
}
