// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.EngineApi.Paris.Handlers;
using Nethermind.Merge.Plugin.EngineApi.Shanghai.Data;

namespace Nethermind.Merge.Plugin.EngineApi.Shanghai.Handlers
{
    /// <summary>
    /// <see href="https://github.com/ethereum/execution-apis/blob/main/src/engine/shanghai.md#engine_getpayloadv2">engine_getpayloadv22</see>.
    /// </summary>
    public class GetPayloadV2Handler : GetPayloadV1AbstractHandler<GetPayloadV2Result>
    {
        public GetPayloadV2Handler(IPayloadPreparationService payloadPreparationService, ILogManager logManager)
            : base(payloadPreparationService, logManager)
        {
        }

        protected override ResultWrapper<GetPayloadV2Result?> ConstructResult(IBlockProductionContext blockContext) =>
            ResultWrapper<GetPayloadV2Result?>.Success(new GetPayloadV2Result { Block = blockContext });
    }
}
