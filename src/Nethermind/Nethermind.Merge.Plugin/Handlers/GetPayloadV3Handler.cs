// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// <a href="https://github.com/ethereum/execution-apis/blob/main/src/engine/experimental/blob-extension.md#engine_getpayloadv3">
/// engine_getpayloadv3</a>
/// </summary>
public class GetPayloadV3Handler : GetPayloadHandlerBase<GetPayloadV3Result>
{
    public GetPayloadV3Handler(IPayloadPreparationService payloadPreparationService, ILogManager logManager) : base(
        3, payloadPreparationService, logManager)
    {
    }

    protected override GetPayloadV3Result GetPayloadResultFromBlock(IBlockProductionContext context) =>
        new(context.CurrentBestBlock!, context.BlockFees, new BlobsBundleV1(context.CurrentBestBlock!));
}
