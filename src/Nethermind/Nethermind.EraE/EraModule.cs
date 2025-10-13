// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Era1.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.EraE;

public class EraModule : Era1.EraModule
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            // Does the importing to IBlockTree/IReceiptStore
            .AddSingleton<Era1.IEraImporter, EraImporter>()

            // Does the exporting to a directory
            .AddSingleton<Era1.IEraExporter, EraExporter>()

            // Create IEraStore which is the main high level reader for other code
            .AddSingleton<Era1.IEraStoreFactory, EraStoreFactory>()

            // Calls IEraImporter or IEraExporter
            .AddSingleton<EraCliRunner>()
            .AddSingleton<Era1.IAdminEraService, AdminEraService>()

            // The admin export/import history method is here
            .RegisterSingletonJsonRpcModule<IEraAdminRpcModule, EraAdminRpcModule>()

            .AddDecorator<IEraConfig>((ctx, eraConfig) =>
            {
                if (string.IsNullOrWhiteSpace(eraConfig.NetworkName))
                {
                    eraConfig.NetworkName = BlockchainIds.GetBlockchainName(ctx.Resolve<ISpecProvider>().NetworkId);
                }
                return eraConfig;
            })
            ;
    }
}
