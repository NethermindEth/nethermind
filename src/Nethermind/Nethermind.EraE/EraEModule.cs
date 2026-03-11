// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.EraE.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.EraE;

public class EraEModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IEraImporter, EraImporter>()
            .AddSingleton<IEraExporter, EraExporter>()
            .AddSingleton<IEraStoreFactory, EraStoreFactory>()
            .AddSingleton<EraCliRunner>()
            .AddSingleton<IAdminEraService, AdminEraService>()
            .RegisterSingletonJsonRpcModule<IEraAdminRpcModule, EraAdminRpcModule>()
            .AddDecorator<IEraEConfig>((ctx, eraConfig) =>
            {
                if (string.IsNullOrWhiteSpace(eraConfig.NetworkName))
                    eraConfig.NetworkName = BlockchainIds.GetBlockchainName(ctx.Resolve<ISpecProvider>().NetworkId);
                return eraConfig;
            });
    }
}
