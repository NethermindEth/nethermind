// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Xdc;

internal sealed class XdcBlockProducerEnvFactory(
    ILifetimeScope rootLifetime,
    IWorldStateManager worldStateManager,
    IBlockProducerTxSourceFactory txSourceFactory)
    : BlockProducerEnvFactory(rootLifetime, worldStateManager, txSourceFactory)
{
    protected override ContainerBuilder ConfigureBuilder(ContainerBuilder builder) =>
        base.ConfigureBuilder(builder)
            .AddModule(new XdcReadOnlyRewardProcessingModule());
}
