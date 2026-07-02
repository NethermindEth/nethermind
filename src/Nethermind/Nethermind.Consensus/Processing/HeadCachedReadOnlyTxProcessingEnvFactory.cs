// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// A read-only tx-processing env factory that serves state reads through the cross-block
/// <see cref="HeadStateCache"/>. Used for the read-only RPC path (eth_call, debug_trace…) only;
/// block processing keeps using the plain <see cref="AutoReadOnlyTxProcessingEnvFactory"/>.
/// </summary>
public sealed class HeadCachedReadOnlyTxProcessingEnvFactory(
    ILifetimeScope parentLifetime,
    IWorldStateManager worldStateManager,
    HeadStateCache headStateCache)
    : AutoReadOnlyTxProcessingEnvFactory(parentLifetime, worldStateManager)
{
    protected override IWorldStateScopeProvider DecorateWorldState(IWorldStateScopeProvider worldState)
        => new HeadStateCacheScopeProvider(worldState, headStateCache);
}
