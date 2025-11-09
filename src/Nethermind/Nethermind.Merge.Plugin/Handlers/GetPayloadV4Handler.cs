// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing.CensorshipDetector;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// <a href="https://github.com/ethereum/execution-apis/blob/main/src/engine/experimental/blob-extension.md#engine_getpayloadv3">
/// engine_getpayloadv3</a>
/// </summary>
public class GetPayloadV4Handler(
    IPayloadPreparationService payloadPreparationService,
    ISpecProvider specProvider,
    ILogManager logManager,
    ICensorshipDetector? censorshipDetector = null,
    IBlockFinder? finder = null)
    : GetPayloadHandlerBase<GetPayloadV4Result>(4, payloadPreparationService, specProvider, logManager, censorshipDetector, finder)
{
    protected override GetPayloadV4Result GetPayloadResultFromBlock(IBlockProductionContext context)
    {
        return new(context.CurrentBestBlock!, context.BlockFees, new BlobsBundleV1(context.CurrentBestBlock!), context.CurrentBestBlock!.ExecutionRequests!, ShouldOverrideBuilder(context.CurrentBestBlock!));
    }
}
