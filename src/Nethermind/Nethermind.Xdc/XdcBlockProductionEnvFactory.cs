// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.State;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc;

public class XdcBlockProductionEnvFactory(ILifetimeScope rootLifetime, IWorldStateManager worldStateManager, IBlockProducerTxSourceFactory txSourceFactory) : BlockProducerEnvFactory(rootLifetime, worldStateManager, txSourceFactory)
{
    protected override ContainerBuilder ConfigureBuilder(ContainerBuilder builder) =>
        // Taiko does not seems to use `BlockProductionTransactionsExecutor`
        base.ConfigureBuilder(builder)
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, XdcTransactionExecutor>();
}
