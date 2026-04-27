// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Autofac;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core.Authentication;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.SszRest.Handlers;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// Bridges the Autofac container (where Engine API domain handlers live) to ASP.NET
/// Core's MS DI container so that <see cref="SszMiddleware"/> and its
/// <see cref="ISszEndpointHandler"/> implementations can be resolved by Kestrel.
/// <para>
/// Registered in Autofac by <c>BaseMergePluginModule</c>; called by
/// <c>JsonRpcRunner</c> during web-host startup via <see cref="IJsonRpcServiceConfigurer"/>.
/// </summary>
public sealed class SszMiddlewareConfigurer(IComponentContext ctx) : IJsonRpcServiceConfigurer
{
    public void Configure(IServiceCollection services)
    {
        services.Bridge<ILogManager>(ctx);
        services.Bridge<IJsonRpcUrlCollection>(ctx);
        services.Bridge<IRpcAuthentication>(ctx);
        services.Bridge<IAsyncHandler<ExecutionPayload, PayloadStatusV1>>(ctx);
        services.Bridge<IAsyncHandler<byte[], ExecutionPayload?>>(ctx);
        services.Bridge<IAsyncHandler<byte[], GetPayloadV2Result?>>(ctx);
        services.Bridge<IAsyncHandler<byte[], GetPayloadV3Result?>>(ctx);
        services.Bridge<IAsyncHandler<byte[], GetPayloadV4Result?>>(ctx);
        services.Bridge<IAsyncHandler<byte[], GetPayloadV5Result?>>(ctx);
        services.Bridge<IAsyncHandler<byte[], GetPayloadV6Result?>>(ctx);

        services.Bridge<IForkchoiceUpdatedHandler>(ctx);

        services.Bridge<IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>>>(ctx);

        services.Bridge<IAsyncHandler<GetBlobsHandlerV2Request, IEnumerable<BlobAndProofV2?>?>>(ctx);

        services.Bridge<IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>>>(ctx);
        services.Bridge<IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV2Result?>>>(ctx);
        services.Bridge<IGetPayloadBodiesByRangeV1Handler>(ctx);
        services.Bridge<IGetPayloadBodiesByRangeV2Handler>(ctx);

        services.Bridge<IHandler<IEnumerable<string>, IEnumerable<string>>>(ctx);
        services.Bridge<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>>(ctx);

        services.AddSingleton<ISszProcessExitSource>(new SszProcessExitSource { ProcessExit = ctx.Resolve<IProcessExitSource>().Token });

        services.AddSingleton<ISszEndpointHandler, NewPayloadSszHandler>();

        services.AddSingleton<ISszEndpointHandler, GetPayloadV1SszHandler>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadV2SszHandler>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadV3SszHandler>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadV4SszHandler>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadV5SszHandler>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadV6SszHandler>();

        services.AddSingleton<ISszEndpointHandler, ForkchoiceUpdatedSszHandler>();

        services.AddSingleton<ISszEndpointHandler, GetBlobsV1SszHandler>();
        services.AddSingleton<ISszEndpointHandler, GetBlobsV2SszHandler>();
        services.AddSingleton<ISszEndpointHandler, GetBlobsV3SszHandler>();

        services.AddSingleton<ISszEndpointHandler, GetPayloadBodiesByHashV1SszHandler>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadBodiesByHashV2SszHandler>();

        services.AddSingleton<ISszEndpointHandler, GetPayloadBodiesByRangeV1SszHandler>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadBodiesByRangeV2SszHandler>();

        services.AddSingleton<ISszEndpointHandler, ClientVersionSszHandler>();
        services.AddSingleton<ISszEndpointHandler, CapabilitiesSszHandler>();
        services.AddSingleton<ISszEndpointHandler, TransitionConfigurationSszHandler>();
    }
}

file static class ServiceCollectionExtensions
{
    public static void Bridge<T>(this IServiceCollection services, IComponentContext ctx) where T : class
        => services.AddSingleton(ctx.Resolve<T>());
}
