// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Autofac;
using Nethermind.Core.Crypto;
using Nethermind.HealthChecks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Core.Test.Modules;

public class MergeRpcModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IEngineRpcModule, EngineRpcModule>()

            .AddSingleton<IAsyncHandler<byte[], ExecutionPayload?>, GetPayloadV1Handler>()
            .AddSingleton<IAsyncHandler<byte[], GetPayloadV2Result?>, GetPayloadV2Handler>()
            .AddSingleton<IAsyncHandler<byte[], GetPayloadV3Result?>, GetPayloadV3Handler>()
            .AddSingleton<IAsyncHandler<byte[], GetPayloadV4Result?>, GetPayloadV4Handler>()
            .AddSingleton<IAsyncHandler<ExecutionPayload, PayloadStatusV1>, NewPayloadHandler>()
            .AddSingleton<IForkchoiceUpdatedHandler, ForkchoiceUpdatedHandler>()
            .AddSingleton<IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>>, GetPayloadBodiesByHashV1Handler>()
            .AddSingleton<IGetPayloadBodiesByRangeV1Handler, GetPayloadBodiesByRangeV1Handler>()
            .AddSingleton<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>, ExchangeTransitionConfigurationV1Handler>()
            .AddSingleton<IHandler<IEnumerable<string>, IEnumerable<string>>, ExchangeCapabilitiesHandler>()
            .AddSingleton<IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>>, GetBlobsHandler>()

            .AddSingleton<IRpcCapabilitiesProvider, EngineRpcCapabilitiesProvider>()
            ;
    }
}
