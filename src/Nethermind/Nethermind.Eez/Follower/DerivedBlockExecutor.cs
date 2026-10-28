// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower.RollupX;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Eez.Follower;

/// <summary>Executes derived blocks on the node's state, outside the main chain.</summary>
public interface IDerivedBlockExecutor
{
    /// <summary>A session that executes the blocks of one batch; each block runs on its parent's committed state.</summary>
    IDerivedBlockSession BeginSession();
}

public interface IDerivedBlockSession : IDisposable
{
    /// <exception cref="EezSettlementException">The block does not execute on its parent's state.</exception>
    ValueTask<Block> ExecuteAsync(BlockHeader parent, DerivedBlock derived, CancellationToken token = default);
}

/// <summary>
/// Builds derived blocks in their own lifetime scope, as the block producer environments do, but with the validation
/// transactions executor: a derived transaction that is invalid fails the block instead of being left out.
/// </summary>
public sealed class DerivedBlockExecutor(ILifetimeScope rootLifetime, IWorldStateManager worldStateManager, DerivedBlockBuilder builder) : IDerivedBlockExecutor
{
    public IDerivedBlockSession BeginSession()
    {
        IWorldStateScopeProvider worldState = worldStateManager.CreateResettableWorldState();
        ILifetimeScope scope = rootLifetime.BeginLifetimeScope(b => b
            .AddScoped(worldState)
            .AddScoped<IReceiptStorage>(NullReceiptStorage.Instance)
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>());
        return OpenSession(scope, builder);
    }

    internal static IDerivedBlockSession OpenSession(ILifetimeScope scope, DerivedBlockBuilder builder) => new Session(scope, builder);

    private sealed class Session(ILifetimeScope scope, DerivedBlockBuilder builder) : IDerivedBlockSession
    {
        private readonly IBlockProcessor _processor = scope.Resolve<IBlockProcessor>();
        private readonly IWorldState _worldState = scope.Resolve<IWorldState>();
        private readonly NativeDerivedSuffix? _native = scope.ResolveOptional<NativeDerivedSuffix>();

        public ValueTask<Block> ExecuteAsync(BlockHeader parent, DerivedBlock derived, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (_native is not null)
            {
                _native.FirstTransaction = derived.NativeEffectsStart;
                _native.DeriveOrigin = derived.NativeOriginFromParent;
            }
            try { return ValueTask.FromResult(builder.Build(parent, derived, _processor, _worldState).Block); }
            finally
            {
                if (_native is not null)
                {
                    _native.FirstTransaction = -1;
                    _native.DeriveOrigin = false;
                }
            }
        }

        public void Dispose() => scope.Dispose();
    }
}
