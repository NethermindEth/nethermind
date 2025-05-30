// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Consensus.Processing.CensorshipDetector;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// <a href="https://github.com/ethereum/execution-apis/blob/main/src/engine/experimental/blob-extension.md#engine_getpayloadv3">
/// engine_getpayloadv3</a>
/// </summary>
public class GetPayloadV3Handler(
    IPayloadPreparationService payloadPreparationService,
    ISpecProvider specProvider,
    ILogManager logManager,
    CensorshipDetector? censorshipDetector = null)
    : GetPayloadHandlerBase<GetPayloadV3Result>(3, payloadPreparationService, specProvider, logManager, censorshipDetector)
{
    protected override GetPayloadV3Result GetPayloadResultFromBlock(IBlockProductionContext context)
    {
        return new(context.CurrentBestBlock!, context.BlockFees, new BlobsBundleV1(context.CurrentBestBlock!), ShouldOverrideBuilder(context.CurrentBestBlock!));
    }
}
