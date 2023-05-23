// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// engine_getPayloadV1
///
/// Given a 8 byte payload_id, it returns the most recent version of an execution payload that is available by the time of the call or responds with an error.
///
/// <see cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#engine_getpayloadv1"/>
/// </summary>
/// <remarks>
/// This call must be responded immediately. An exception would be the case when no version of the payload is ready yet
/// and in this case there might be a slight delay before the response is done.
/// Execution client should create a payload with empty transaction set to be able to respond as soon as possible.
/// If there were no prior engine_preparePayload call with the corresponding payload_id or the process of building
/// a payload has been cancelled due to the timeout then execution client must respond with error message.
/// Execution client may stop the building process with the corresponding payload_id value after serving this call.
/// </remarks>
public class GetPayloadV1Handler : GetPayloadHandlerBase<ExecutionPayload>
{
    public GetPayloadV1Handler(IPayloadPreparationService payloadPreparationService, ILogManager logManager) : base(
        1, payloadPreparationService, logManager)
    {
    }

    protected override ExecutionPayload GetPayloadResultFromBlock(IBlockProductionContext context) =>
        new(context.CurrentBestBlock!);
}
