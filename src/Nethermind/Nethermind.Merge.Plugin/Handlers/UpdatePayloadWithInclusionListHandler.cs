// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.BlockProduction;

namespace Nethermind.Merge.Plugin.Handlers;

public class UpdatePayloadWithInclusionListHandler(
    IPayloadPreparationService payloadPreparationService,
    InclusionListTxSource? inclusionListTxSource,
    ISpecProvider specProvider)
    : IHandler<(string payloadId, byte[][] inclusionListTransactions), string?>
{
    public ResultWrapper<string?> Handle((string payloadId, byte[][] inclusionListTransactions) args)
    {
        BlockHeader? header = payloadPreparationService.GetPayloadHeader(args.payloadId);
        if (header is null)
        {
            return ResultWrapper<string?>.Fail($"Could not find existing payload with id {args.payloadId}.");
        }

        inclusionListTxSource?.Set(args.inclusionListTransactions, specProvider.GetSpec(header));
        payloadPreparationService.ForceRebuildPayload(args.payloadId);
        return ResultWrapper<string?>.Success(args.payloadId);
    }
}
