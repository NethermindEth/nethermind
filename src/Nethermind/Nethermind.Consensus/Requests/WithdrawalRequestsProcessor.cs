// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Requests;

// https://eips.ethereum.org/EIPS/eip-7002#block-processing
public class WithdrawalRequestsProcessor(ITransactionProcessor transactionProcessor) : RequestProcessor<WithdrawalRequest>(transactionProcessor), IRequestProcessor<WithdrawalRequest>
{
    private const int SizeOfClass = 20 + 48 + 8;

    public IEnumerable<WithdrawalRequest> ReadRequests(Block block, IWorldState state, IReleaseSpec spec)
    {
        return ReadRequests(block, state, spec, spec.Eip7002ContractAddress);
    }

    protected override bool IsEnabledInSpec(IReleaseSpec spec)
    {
        return spec.WithdrawalRequestsEnabled;
    }

    protected override IEnumerable<WithdrawalRequest> ParseResult(Memory<byte> result)
    {
        int count = result.Length / SizeOfClass;
        for (int i = 0; i < count; ++i)
        {
            int offset = i * SizeOfClass;
            WithdrawalRequest request = new()
            {
                SourceAddress = new Address(result.Slice(offset, 20).AsArray()),
                ValidatorPubkey = result.Slice(offset + 20, 48),
                Amount = BinaryPrimitives.ReadUInt64BigEndian(result.Slice(offset + 68, 8).Span)
            };
            yield return request;
        }
    }
}
