// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.Xdc;

/// <summary>
/// Builds a read-only transaction environment that can read a state root still held by XDC state sync.
/// </summary>
internal sealed class XdcSyncReadOnlyTxProcessingEnvFactory(
    ILifetimeScope parentLifetime,
    IWorldStateManager worldStateManager,
    IPersistence persistence,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    ILogManager logManager) : IReadOnlyTxProcessingEnvFactory
{
    public IReadOnlyTxProcessorSource Create()
    {
        IWorldStateScopeProvider normalProvider = worldStateManager.CreateResettableWorldState();
        XdcSyncWorldStateScopeProvider worldState = new(normalProvider, persistence, codeDb, logManager);

        ILifetimeScope? childScope = null;
        try
        {
            childScope = parentLifetime.BeginLifetimeScope(builder =>
            {
                builder
                    .AddSingleton<IWorldStateScopeProvider>(worldState, takeOwnership: true)
                    .AddSingleton<AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv>();
            });

            return childScope!.Resolve<AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv>();
        }
        catch
        {
            try
            {
                childScope?.Dispose();
            }
            finally
            {
                worldState.Dispose();
            }
            throw;
        }
    }
}
