// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Requests;

// https://eips.ethereum.org/EIPS/eip-7251#block-processing
public class ConsolidationRequestsProcessor(ITransactionProcessor transactionProcessor) : RequestProcessor<ConsolidationRequest>(transactionProcessor), IRequestProcessor<ConsolidationRequest>
{
    private const int SizeOfClass = 20 + 48 + 48;

    public IEnumerable<ConsolidationRequest> ReadRequests(Block block, IWorldState state, IReleaseSpec spec)
    {
        return base.ReadRequests(block, state, spec, spec.Eip7251ContractAddress);
    }
    protected override bool IsEnabledInSpec(IReleaseSpec spec)
    {
        return spec.ConsolidationRequestsEnabled;
    }
    protected override IEnumerable<ConsolidationRequest> ParseResult(Memory<byte> result)
    {
        int count = result.Length / SizeOfClass;

        for (int i = 0; i < count; ++i)
        {
            int offset = i * SizeOfClass;
            ConsolidationRequest request = new()
            {
                SourceAddress = new Address(result.Slice(offset, 20).ToArray()),
                SourcePubkey = result.Slice(offset + 20, 48),
                TargetPubkey = result.Slice(offset + 68, 48)
            };

            yield return request;
        }
    }
}
