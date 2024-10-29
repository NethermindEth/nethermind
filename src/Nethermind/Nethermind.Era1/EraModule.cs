// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Era1;

public class EraModule: Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IEraImporter, EraImporter>()
            .AddSingleton<IEraExporter, EraExporter>()
            .AddSingleton<IEraStoreFactory, EraStoreFactory>()
            .AddSingleton<EraCliRunner>();

        builder
            .Register(ctx => BlockchainIds.GetBlockchainName(ctx.Resolve<ISpecProvider>().NetworkId))
            .Keyed<string>(EraComponentKeys.NetworkName);
    }
}
