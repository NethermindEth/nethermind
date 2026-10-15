// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary><c>engine_getPayloadV7</c>: the Bogota payload with the builder's EIP-8369 inclusion-list claims.</summary>
public class GetPayloadV7Handler(
    IPayloadPreparationService payloadPreparationService,
    ISpecProvider specProvider,
    ILogManager logManager,
    IBuilderOverridePolicy builderOverridePolicy)
    : GetPayloadHandlerBase<GetPayloadV7Result>(EngineApiVersions.GetPayload.V7, payloadPreparationService, specProvider, logManager, builderOverridePolicy)
{
    protected override GetPayloadV7Result GetPayloadResultFromBlock(IBlockProductionContext context) => new GetPayloadV7DirectResponse(context.CurrentBestBlock!, context.BlockFees, new BlobsBundleV2(context.CurrentBestBlock!), context.CurrentBestBlock!.ExecutionRequests!, ShouldOverrideBuilder(context.CurrentBestBlock!));
}
