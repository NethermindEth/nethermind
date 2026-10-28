// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Eez.Follower.RollupX;

internal sealed class NativeDerivedBlockExecutor(ILifetimeScope rootLifetime, IWorldStateManager worldStates,
    DerivedBlockBuilder builder, NativeCanonicalHistory history) : IDerivedBlockExecutor
{
    public IDerivedBlockSession BeginSession()
    {
        NativeL1HeaderChain captured = history.Read();
        IWorldStateScopeProvider state = worldStates.CreateResettableWorldState();
        ILifetimeScope scope = rootLifetime.BeginLifetimeScope(b => b
            .AddScoped(state)
            .AddScoped(captured)
            .AddScoped<IReceiptStorage>(NullReceiptStorage.Instance)
            .AddScoped<NativeDerivedSuffix>()
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, NativeDerivedTransactionsExecutor>());
        return DerivedBlockExecutor.OpenSession(scope, builder);
    }
}
