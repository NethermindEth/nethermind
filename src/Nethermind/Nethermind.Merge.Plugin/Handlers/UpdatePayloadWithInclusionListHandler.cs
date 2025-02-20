// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Transactions;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.BlockProduction;

namespace Nethermind.Merge.Plugin.Handlers;

public class UpdatePayloadWithInclusionListHandler(IPayloadPreparationService payloadPreparationService, InclusionListTxSource? inclusionListTxSource)
    : IHandler<(string payloadId, byte[][] inclusionListTransactions), string?>
{
    public ResultWrapper<string?> Handle((string payloadId, byte[][] inclusionListTransactions) args)
    {
        inclusionListTxSource?.Set(args.inclusionListTransactions);
        payloadPreparationService.ForceRebuildPayload(args.payloadId);
        return ResultWrapper<string?>.Success(args.payloadId);
    }
}
