// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// <a href="https://github.com/ethereum/execution-apis/blob/main/src/engine/experimental/blob-extension.md#engine_getpayloadv3">
/// engine_getpayloadv3</a>
/// </summary>
public class GetPayloadV4Handler : GetPayloadHandlerBase<GetPayloadV4Result>
{
    public GetPayloadV4Handler(IPayloadPreparationService payloadPreparationService, ISpecProvider specProvider, ILogManager logManager) : base(
        4, payloadPreparationService, specProvider, logManager)
    {
    }

    protected override GetPayloadV4Result GetPayloadResultFromBlock(IBlockProductionContext context) =>
        new(context.CurrentBestBlock!, context.BlockFees, new BlobsBundleV1(context.CurrentBestBlock!));
}
