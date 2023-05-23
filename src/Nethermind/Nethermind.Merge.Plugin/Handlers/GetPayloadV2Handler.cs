// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// <see href="https://github.com/ethereum/execution-apis/blob/main/src/engine/shanghai.md#engine_getpayloadv2">
/// engine_getpayloadv2</see>.
/// </summary>
public class GetPayloadV2Handler : GetPayloadHandlerBase<GetPayloadV2Result>
{
    public GetPayloadV2Handler(IPayloadPreparationService payloadPreparationService, ILogManager logManager) : base(
        2, payloadPreparationService, logManager)
    {
    }

    protected override GetPayloadV2Result GetPayloadResultFromBlock(IBlockProductionContext context) =>
        new(context.CurrentBestBlock!, context.BlockFees);
}
