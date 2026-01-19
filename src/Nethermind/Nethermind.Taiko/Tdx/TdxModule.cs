// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Taiko.Config;

namespace Nethermind.Taiko.Tdx;

/// <summary>
/// Autofac module for TDX attestation services.
/// Services are registered regardless of config; RPC checks config at runtime.
/// </summary>
public class TdxModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder.AddSingleton<ITdxsClient, TdxsClient>();

        // Register TDX service - returns NullTdxService when disabled
        builder.Register<ITdxService>(ctx =>
        {
            ISurgeConfig surgeConfig = ctx.Resolve<ISurgeConfig>();
            if (!surgeConfig.TdxEnabled)
                return NullTdxService.Instance;

            return new TdxService(
                ctx.Resolve<ISurgeTdxConfig>(),
                ctx.Resolve<ITdxsClient>(),
                ctx.Resolve<ILogManager>());
        }).SingleInstance();

        builder.RegisterSingletonJsonRpcModule<ITdxRpcModule, TdxRpcModule>();
    }
}
