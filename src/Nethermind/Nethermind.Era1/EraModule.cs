// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Era1.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Era1;

public class EraModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            // Does the importing to IBlockTree/IReceiptStore
            .AddSingleton<IEraImporter, EraImporter>()

            // Does the exporting to a directory
            .AddSingleton<IEraExporter, EraExporter>()

            // Create IEraStore which is the main high level reader for other code
            .AddSingleton<IEraStoreFactory, EraStoreFactory>()

            // Calls IEraImporter or IEraExporter
            .AddSingleton<EraCliRunner>()
            .AddSingleton<IAdminEraService, AdminEraService>()

            // The admin export/import history method is here
            .RegisterSingletonJsonRpcModule<IEraAdminRpcModule, EraAdminRpcModule>()
            ;

        builder.RegisterBuildCallback((ctx) =>
        {
            IEraConfig eraConfig = ctx.Resolve<IEraConfig>();
            if (string.IsNullOrWhiteSpace(eraConfig.NetworkName))
            {
                eraConfig.NetworkName = BlockchainIds.GetBlockchainName(ctx.Resolve<ISpecProvider>().NetworkId);
            }
        });
    }
}
